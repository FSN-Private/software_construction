using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryLifecycleHistoryBinding(Guid PointId, Dictionary<string, string> Pins);

internal static class RecoveryLifecycleHistoryBindings
{
    private static readonly string[] PinNames = ["ConfigurationDigest", "ApiBuildDigest", "SourceRevision", "MigrationDigest",
        "RecoveryWorkerBuildDigest", "RecoveryTemplateDigest", "SourceTemplateDigest", "OwnerUserPoolId",
        "OwnerUserPoolClientId", "OwnerUserPoolIssuer", "MigrationDirectory", "IngestWorkerBuildDigest", "RendererBuildDigest",
        "DatabaseRootCertificateSha256"];

    internal static async Task<IReadOnlyList<RecoveryLifecycleHistoryBinding>> ReadAsync(
        RecoveryWorkerConfiguration configuration, CancellationToken ct)
    {
        var bytes = await RecoveryPurgeRunner.ReadBoundedArtifactAsync(configuration.Required("HoldHistoryBindingsFile"),
            64 * 1024 * 1024, ct);
        RecoveryLifecycleHistoryBinding[] bindings;
        try
        {
            bindings = JsonSerializer.Deserialize<RecoveryLifecycleHistoryBinding[]>(bytes,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new JsonException();
        }
        catch (JsonException error) { throw new RecoveryWorkerException("hold_history_bindings_invalid", error); }
        Validate(bindings);
        return bindings;
    }

    internal static void Validate(IReadOnlyList<RecoveryLifecycleHistoryBinding> bindings)
    {
        if (bindings.Count is < 1 or > 65_536 || bindings.Any(item => item is null || item.PointId == Guid.Empty ||
                item.Pins is null || item.Pins.Count != PinNames.Length ||
                item.Pins.Keys.Except(PinNames, StringComparer.Ordinal).Any() ||
                item.Pins.Values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 4096)) ||
            bindings.Select(item => item.PointId).Distinct().Count() != bindings.Count)
            throw new RecoveryWorkerException("hold_history_bindings_invalid");
        foreach (var binding in bindings)
        {
            foreach (var name in PinNames.Where(name => name.EndsWith("Digest", StringComparison.Ordinal) || name.EndsWith("Sha256", StringComparison.Ordinal)))
                if (binding.Pins[name].Length != 64 || binding.Pins[name].Any(c => !char.IsAsciiHexDigit(c)) ||
                    binding.Pins[name] != binding.Pins[name].ToLowerInvariant())
                    throw new RecoveryWorkerException("hold_history_binding_digest_invalid");
        }
    }

    internal static async Task<IReadOnlyList<CopyPointer>> ReadCompleteInventoryAsync(
        RecoveryWorkerConfiguration configuration, IAmazonDynamoDB dynamo, IAmazonS3 s3, RecoverySigningTrust trust,
        IReadOnlyList<RecoveryLifecycleHistoryBinding> bindings, CancellationToken ct)
    {
        Validate(bindings);
        var owner = configuration.Required("OwnerId");
        var library = configuration.Required("LibraryId");
        var table = configuration.Required("ControlTableName");
        var key = RecoveryPointIndexBootstrapRunner.IndexKey(owner, library);
        var marker = (await dynamo.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true }, ct)).Item;
        RecoveryPointIndexBootstrapRunner.ValidateComplete(marker);
        if (!marker.TryGetValue("indexedPointCount", out var count) ||
            !long.TryParse(count.N, NumberStyles.None, CultureInfo.InvariantCulture, out var total) || total != bindings.Count)
            throw new RecoveryWorkerException("hold_history_inventory_incomplete");
        var pointers = new List<CopyPointer>();
        foreach (var binding in bindings.OrderBy(item => item.PointId))
        {
            // Every distinct member must exist in the configured owner partition.
            // Matching its trusted indexed count establishes completeness without Query.
            var point = await RecoveryLatestPointDiscovery.ReadIndexedPointAsync(dynamo, s3, table,
                owner, library, binding.PointId, trust, ct);
            pointers.Add(point.Pointer);
        }
        var after = (await dynamo.GetItemAsync(new GetItemRequest { TableName = table, Key = key, ConsistentRead = true }, ct)).Item;
        if (!RecoveryEvidenceCodec.SerializeCanonical(marker).AsSpan().SequenceEqual(RecoveryEvidenceCodec.SerializeCanonical(after)))
            throw new RecoveryWorkerException("hold_release_history_changed");
        return pointers;
    }
}
