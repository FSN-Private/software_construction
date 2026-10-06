using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryPointIndexBootstrapRunner
{
    private const int MaximumScannedItems = 1_000_000;
    private static readonly TimeSpan BootstrapTimeout = TimeSpan.FromMinutes(15);

    public static async Task RunAsync(RecoveryWorkerConfiguration configuration, CancellationToken cancellationToken)
    {
        var region = configuration.RequiredRegion("DestinationRegion");
        using var sts = new AmazonSecurityTokenServiceClient(Amazon.RegionEndpoint.GetBySystemName(region));
        var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken);
        if (identity.Account != configuration.RequiredAccountId("DestinationAccountId"))
            throw new RecoveryWorkerException("recovery_task_account_mismatch");

        var ownerId = configuration.Optional("OwnerId");
        var libraryId = configuration.Optional("LibraryId");
        if (ownerId is not null && libraryId is null)
            throw new RecoveryWorkerException("configured_scope_incomplete");
        using var dynamo = new AmazonDynamoDBClient(Amazon.RegionEndpoint.GetBySystemName(region));
        await BootstrapAsync(dynamo, configuration.Required("ControlTableName"), ownerId, libraryId, cancellationToken);
    }

    internal static async Task BootstrapAsync(IAmazonDynamoDB dynamo, string table,
        string? configuredOwnerId, string? configuredLibraryId, CancellationToken cancellationToken)
    {
        if (configuredOwnerId is not null && configuredLibraryId is null)
            throw new RecoveryWorkerException("configured_scope_incomplete");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BootstrapTimeout);
        var rowsByScope = new Dictionary<ScopeKey, Dictionary<Guid, string>>();
        Dictionary<string, AttributeValue>? startKey = null;
        var scanned = 0;
        do
        {
            var page = await dynamo.ScanAsync(new ScanRequest
            {
                TableName = table,
                ConsistentRead = true,
                Limit = 1000,
                ExclusiveStartKey = startKey
            }, timeout.Token);
            scanned = checked(scanned + Math.Max(page.ScannedCount ?? 0, page.Items.Count));
            if (scanned > MaximumScannedItems) throw new RecoveryWorkerException("scope_index_discovery_limit");
            foreach (var item in page.Items)
            {
                if (!TryReadPoint(item, out var ownerId, out var libraryId, out var pointId, out var manifestSha))
                    continue;
                var key = new ScopeKey(ownerId, libraryId);
                if (!rowsByScope.TryGetValue(key, out var points)) rowsByScope.Add(key, points = []);
                if (points.TryGetValue(pointId, out var previous) && previous != manifestSha)
                    throw new RecoveryWorkerException("scope_index_point_conflict");
                points[pointId] = manifestSha;
            }
            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        } while (startKey is not null);

        if (configuredOwnerId is not null && configuredLibraryId is not null)
        {
            try
            {
                _ = RecoveryOwnerPointIndex.ScopeHash(configuredOwnerId, configuredLibraryId);
            }
            catch (InvalidDataException exception)
            {
                throw new RecoveryWorkerException("configured_scope_invalid", exception);
            }
            rowsByScope.TryAdd(new(configuredOwnerId, configuredLibraryId), []);
        }
        if (rowsByScope.Count == 0)
            throw new RecoveryWorkerException("scope_index_bootstrap_scope_missing");

        foreach (var scope in rowsByScope.Keys.OrderBy(item => item.OwnerId, StringComparer.Ordinal)
                     .ThenBy(item => item.LibraryId, StringComparer.Ordinal))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await BootstrapScopeAsync(dynamo, table, scope, rowsByScope[scope], timeout.Token);
        }
    }

    private static async Task BootstrapScopeAsync(IAmazonDynamoDB dynamo, string table, ScopeKey scope,
        IReadOnlyDictionary<Guid, string> points, CancellationToken cancellationToken)
    {
        var markerKey = IndexKey(scope.OwnerId, scope.LibraryId);
        var marker = await GetAsync(dynamo, table, markerKey, cancellationToken);
        if (marker is { Count: > 0 })
        {
            if (GetString(marker, "status") == "complete")
            {
                ValidateComplete(marker);
                await ValidateIndexedScopeAsync(dynamo, table, scope, marker, points, cancellationToken);
                return;
            }
            if (GetString(marker, "status") != "building" || GetNumber(marker, "indexVersion") != 1)
                throw new RecoveryWorkerException("scope_index_marker_invalid");
        }
        else
        {
            try
            {
                await dynamo.PutItemAsync(new PutItemRequest
                {
                    TableName = table,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = S(markerKey["pk"].S),
                        ["sk"] = S(markerKey["sk"].S),
                        ["indexVersion"] = N(1),
                        ["status"] = S("building"),
                        ["buildingAtUtc"] = S(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
                    },
                    ConditionExpression = "attribute_not_exists(pk)"
                }, cancellationToken);
            }
            catch (ConditionalCheckFailedException)
            {
                marker = await GetAsync(dynamo, table, markerKey, cancellationToken);
                if (marker is not { Count: > 0 }) throw new RecoveryWorkerException("scope_index_marker_missing");
                if (GetString(marker, "status") == "complete")
                {
                    ValidateComplete(marker);
                    await ValidateIndexedScopeAsync(dynamo, table, scope, marker, points, cancellationToken);
                    return;
                }
                if (GetString(marker, "status") != "building" || GetNumber(marker, "indexVersion") != 1)
                    throw new RecoveryWorkerException("scope_index_marker_invalid");
            }
        }

        var pointIds = new List<Guid>(points.Count);
        foreach (var point in points.OrderBy(pair => pair.Key))
        {
            var pointKey = OwnerPointKey(scope.OwnerId, scope.LibraryId, point.Key);
            var item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = S(pointKey["pk"].S),
                ["sk"] = S(pointKey["sk"].S),
                ["pointId"] = S(point.Key.ToString("D").ToLowerInvariant()),
                ["sourceManifestSha256"] = S(point.Value),
                ["bootstrapMember"] = Bool(true)
            };
            try
            {
                await dynamo.PutItemAsync(new PutItemRequest
                {
                    TableName = table,
                    Item = item,
                    ConditionExpression = "attribute_not_exists(pk) OR (sourceManifestSha256=:manifest AND bootstrapMember=:true)",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":manifest"] = S(point.Value),
                        [":true"] = Bool(true)
                    }
                }, cancellationToken);
            }
            catch (ConditionalCheckFailedException)
            {
                var existing = await GetAsync(dynamo, table, pointKey, cancellationToken);
                if (existing is not { Count: > 0 } || GetString(existing, "sourceManifestSha256") != point.Value ||
                    GetBool(existing, "bootstrapMember") != true)
                    throw new RecoveryWorkerException("scope_index_point_conflict");
            }
            pointIds.Add(point.Key);
        }

        var bootstrapDigest = RecoveryOwnerPointIndex.ComputeBootstrapPointSetSha256(scope.OwnerId,
            scope.LibraryId, pointIds);
        var expectedPoints = points.ToDictionary(pair => pair.Key, pair => pair.Value);
        var indexedBeforeComplete = await ReadIndexedMembersAsync(dynamo, table, scope, cancellationToken);
        try
        {
            RecoveryOwnerPointIndex.ValidateStrongIndex(scope.OwnerId, scope.LibraryId, pointIds.Count,
                bootstrapDigest, pointIds.Count, indexedBeforeComplete, expectedPoints);
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("scope_index_bootstrap_inventory_mismatch", exception);
        }
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = table,
                Key = markerKey,
                UpdateExpression = "SET #status=:complete, bootstrapPointSetSha256=:digest, bootstrapPointCount=:count, indexedPointCount=:count REMOVE buildingAtUtc",
                ConditionExpression = "#status=:building AND indexVersion=:version",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":complete"] = S("complete"),
                    [":building"] = S("building"),
                    [":version"] = N(1),
                    [":digest"] = S(bootstrapDigest),
                    [":count"] = N(pointIds.Count)
                }
            }, cancellationToken);
        }
        catch (ConditionalCheckFailedException)
        {
            var completed = await GetAsync(dynamo, table, markerKey, cancellationToken);
            if (completed is null || completed.Count == 0)
                throw new RecoveryWorkerException("scope_index_bootstrap_race");
            if (GetString(completed, "status") != "complete" ||
                GetString(completed, "bootstrapPointSetSha256") != bootstrapDigest ||
                GetNumber(completed, "bootstrapPointCount") != pointIds.Count)
                throw new RecoveryWorkerException("scope_index_bootstrap_race");
        }

        var finalMarker = await GetAsync(dynamo, table, markerKey, cancellationToken);
        if (finalMarker is not { Count: > 0 }) throw new RecoveryWorkerException("scope_index_bootstrap_missing");
        await ValidateIndexedScopeAsync(dynamo, table, scope, finalMarker, points, cancellationToken);
    }

    private static async Task ValidateIndexedScopeAsync(IAmazonDynamoDB dynamo, string table, ScopeKey scope,
        Dictionary<string, AttributeValue> marker, IReadOnlyDictionary<Guid, string> discoveredPoints,
        CancellationToken cancellationToken)
    {
        ValidateComplete(marker);
        var indexed = await ReadIndexedMembersAsync(dynamo, table, scope, cancellationToken);
        try
        {
            var baselineCount = checked((int)GetNumber(marker, "bootstrapPointCount"));
            RecoveryOwnerPointIndex.ValidateStrongIndex(scope.OwnerId, scope.LibraryId, baselineCount,
                GetString(marker, "bootstrapPointSetSha256") ?? "", GetNumber(marker, "indexedPointCount"),
                indexed, discoveredPoints);
        }
        catch (InvalidDataException exception)
        {
            throw new RecoveryWorkerException("scope_index_inventory_mismatch", exception);
        }
        catch (OverflowException exception)
        {
            throw new RecoveryWorkerException("scope_index_count_invalid", exception);
        }
    }

    private static async Task<List<RecoveryOwnerPointMembership>> ReadIndexedMembersAsync(
        IAmazonDynamoDB dynamo, string table, ScopeKey scope, CancellationToken cancellationToken)
    {
        var indexed = new List<RecoveryOwnerPointMembership>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var page = await dynamo.QueryAsync(new QueryRequest
            {
                TableName = table,
                ConsistentRead = true,
                KeyConditionExpression = "pk=:pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = S(RecoveryOwnerPointIndex.PartitionKey(scope.OwnerId, scope.LibraryId))
                },
                ExclusiveStartKey = startKey,
                Limit = 1000
            }, cancellationToken);
            foreach (var item in page.Items)
            {
                var sortKey = GetString(item, "sk");
                if (sortKey == "INDEX") continue;
                if (sortKey is null || !sortKey.StartsWith("POINT#", StringComparison.Ordinal) ||
                    !Guid.TryParseExact(sortKey.AsSpan("POINT#".Length), "D", out var pointId) ||
                    pointId == Guid.Empty || GetString(item, "pointId") != pointId.ToString("D").ToLowerInvariant() ||
                    GetString(item, "sourceManifestSha256") is not { } digest || !IsHash(digest) ||
                    GetBool(item, "bootstrapMember") is not { } bootstrapMember)
                    throw new RecoveryWorkerException("scope_index_member_invalid");

                indexed.Add(new(pointId, digest, bootstrapMember));
            }
            startKey = page.LastEvaluatedKey is { Count: > 0 } next ? next : null;
        } while (startKey is not null);
        return indexed;
    }

    internal static async Task<IReadOnlyList<TransactWriteItem>> BuildNewPointAdmissionAsync(
        IAmazonDynamoDB dynamo, string table, string ownerId, string libraryId, Guid pointId,
        string sourceManifestSha256, CancellationToken cancellationToken)
    {
        var markerKey = IndexKey(ownerId, libraryId);
        var marker = await GetAsync(dynamo, table, markerKey, cancellationToken);
        if (marker is not { Count: > 0 }) throw new RecoveryWorkerException("scope_index_bootstrap_required");
        ValidateComplete(marker);
        var memberKey = OwnerPointKey(ownerId, libraryId, pointId);
        var member = await GetAsync(dynamo, table, memberKey, cancellationToken);
        if (member is { Count: > 0 })
        {
            if (GetString(member, "pointId") != pointId.ToString("D").ToLowerInvariant() ||
                GetString(member, "sourceManifestSha256") != sourceManifestSha256 ||
                GetBool(member, "bootstrapMember") is null)
                throw new RecoveryWorkerException("recovery_point_id_conflict");
            return BuildPointIndexAdmissionActions(table, ownerId, libraryId, pointId,
                sourceManifestSha256, alreadyIndexed: true);
        }

        return BuildPointIndexAdmissionActions(table, ownerId, libraryId, pointId,
            sourceManifestSha256, alreadyIndexed: false);
    }

    internal static IReadOnlyList<TransactWriteItem> BuildPointIndexAdmissionActions(string table,
        string ownerId, string libraryId, Guid pointId, string sourceManifestSha256, bool alreadyIndexed)
    {
        if (pointId == Guid.Empty || !IsHash(sourceManifestSha256))
            throw new RecoveryWorkerException("scope_index_admission_invalid");
        var markerKey = IndexKey(ownerId, libraryId);
        var memberKey = OwnerPointKey(ownerId, libraryId, pointId);
        if (alreadyIndexed)
            return
            [
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = markerKey,
                        ConditionExpression = "#status=:complete AND indexVersion=:version",
                        ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":complete"] = S("complete"),
                            [":version"] = N(1)
                        }
                    }
                },
                new TransactWriteItem
                {
                    ConditionCheck = new ConditionCheck
                    {
                        TableName = table,
                        Key = memberKey,
                        ConditionExpression = "sourceManifestSha256=:manifest",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":manifest"] = S(sourceManifestSha256)
                        }
                    }
                }
            ];
        return
        [
            new TransactWriteItem
            {
                Put = new Put
                {
                    TableName = table,
                    Item = new Dictionary<string, AttributeValue>
                    {
                        ["pk"] = S(memberKey["pk"].S),
                        ["sk"] = S(memberKey["sk"].S),
                        ["pointId"] = S(pointId.ToString("D").ToLowerInvariant()),
                        ["sourceManifestSha256"] = S(sourceManifestSha256),
                        ["bootstrapMember"] = Bool(false)
                    },
                    ConditionExpression = "attribute_not_exists(pk)"
                }
            },
            new TransactWriteItem
            {
                Update = new Update
                {
                    TableName = table,
                    Key = markerKey,
                    UpdateExpression = "ADD indexedPointCount :one",
                    ConditionExpression = "#status=:complete AND indexVersion=:version",
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":one"] = N(1),
                        [":complete"] = S("complete"),
                        [":version"] = N(1)
                    }
                }
            }
        ];
    }

    internal static Dictionary<string, AttributeValue> IndexKey(string ownerId, string libraryId) =>
        Key(RecoveryOwnerPointIndex.PartitionKey(ownerId, libraryId), "INDEX");

    internal static Dictionary<string, AttributeValue> OwnerPointKey(string ownerId, string libraryId, Guid pointId) =>
        Key(RecoveryOwnerPointIndex.PartitionKey(ownerId, libraryId), RecoveryOwnerPointIndex.PointSortKey(pointId));

    internal static void ValidateComplete(Dictionary<string, AttributeValue> marker)
    {
        if (GetNumber(marker, "indexVersion") != 1 || GetString(marker, "status") != "complete" ||
            GetNumber(marker, "bootstrapPointCount") < 0 ||
            GetNumber(marker, "indexedPointCount") < GetNumber(marker, "bootstrapPointCount") ||
            !IsHash(GetString(marker, "bootstrapPointSetSha256")))
            throw new RecoveryWorkerException("scope_index_marker_invalid");
    }

    private static async Task<Dictionary<string, AttributeValue>?> GetAsync(IAmazonDynamoDB dynamo, string table,
        Dictionary<string, AttributeValue> key, CancellationToken cancellationToken)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = table,
            Key = key,
            ConsistentRead = true
        }, cancellationToken);
        return response.Item is { Count: > 0 } item ? item : null;
    }

    private static bool TryReadPoint(Dictionary<string, AttributeValue> item, out string ownerId,
        out string libraryId, out Guid pointId, out string manifestSha)
    {
        ownerId = libraryId = manifestSha = string.Empty;
        pointId = Guid.Empty;
        if (GetString(item, "pk") is not { } pk || !pk.StartsWith("POINT#", StringComparison.Ordinal) ||
            GetString(item, "sk") != "COPY") return false;
        if (!Guid.TryParseExact(pk.AsSpan("POINT#".Length), "D", out pointId) || pointId == Guid.Empty ||
            GetString(item, "ownerId") is not { } owner || GetString(item, "libraryId") is not { } library ||
            GetString(item, "sourceManifestSha256") is not { } digest || !IsHash(digest))
            throw new RecoveryWorkerException("scope_index_legacy_point_invalid");
        ownerId = owner;
        libraryId = library;
        manifestSha = digest;
        return true;
    }

    private static Dictionary<string, AttributeValue> Key(string pk, string sk) => new()
    {
        ["pk"] = S(pk),
        ["sk"] = S(sk)
    };

    private static string? GetString(Dictionary<string, AttributeValue>? item, string name) =>
        item is not null && item.TryGetValue(name, out var value) ? value.S : null;

    private static long GetNumber(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && long.TryParse(value.N, NumberStyles.None,
            CultureInfo.InvariantCulture, out var number) ? number : -1;

    private static bool? GetBool(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) ? value.BOOL : null;

    private static bool IsHash(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static AttributeValue S(string value) => new() { S = value };
    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
    private static AttributeValue Bool(bool value) => new() { BOOL = value };

    private sealed record ScopeKey(string OwnerId, string LibraryId);
}
