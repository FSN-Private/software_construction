using System.Globalization;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.KeyManagementService;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryControlPoint(Guid PointId,string Status,DateTimeOffset? CapturedAtUtc,
    DateTimeOffset? SnapshotCutoffUtc,DateTimeOffset? VerifiedAtUtc,string? ManifestSha256,
    IReadOnlyList<RecoveryCatalogCoverageV1> Coverage);
internal sealed record RecoveryControlHealth(string Status,RecoveryControlPoint? Point,double? PointAgeSeconds);
internal sealed record RecoveryControlPage(IReadOnlyList<RecoveryControlPoint> Points,Guid? NextAfterPointId);

/// <summary>Independent owner-scoped observation reader. It never signs, publishes,
/// fetches database secrets, reads primary resources or launches a restore.</summary>
internal static class RecoveryControlReader
{
    internal static async Task RunAsync(RecoveryWorkerConfiguration configuration,CancellationToken ct)
    {
        if(configuration.Required("RecoveryOnly")!="true") throw new RecoveryWorkerException("control_recovery_only_required");
        var region=Amazon.RegionEndpoint.GetBySystemName(configuration.RequiredRegion("DestinationRegion"));
        using var sts=new AmazonSecurityTokenServiceClient(region);
        if((await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(),ct)).Account!=configuration.RequiredAccountId("DestinationAccountId"))
            throw new RecoveryWorkerException("recovery_task_account_mismatch");
        using var dynamo=new AmazonDynamoDBClient(region);
        using var kms=new AmazonKeyManagementServiceClient(region);
        // Creation reads/pins the public key; this path never calls Sign.
        var signer=await AwsKmsEvidenceSigner.CreateAsync(kms,configuration.Required("SigningKeyId"),
            configuration.RequiredHash("SigningPublicKeySha256"),ct);
        object result;
        if(configuration.Mode=="health") result=await ReadHealthAsync(dynamo,configuration,signer.Trust,DateTimeOffset.UtcNow,ct);
        else
        {
            var limit=20;
            if(configuration.Optional("PointLimit") is { } rawLimit &&
                (!int.TryParse(rawLimit,NumberStyles.None,CultureInfo.InvariantCulture,out limit) || limit is <1 or >100))
                throw new RecoveryWorkerException("control_point_limit_invalid");
            Guid? after=null;
            if(configuration.Optional("AfterPointId") is { } rawAfter)
            {
                if(!Guid.TryParseExact(rawAfter,"D",out var parsed) || parsed==Guid.Empty)
                    throw new RecoveryWorkerException("control_point_cursor_invalid");
                after=parsed;
            }
            result=await ReadPageAsync(dynamo,configuration,signer.Trust,limit,after,ct);
        }
        Console.WriteLine(JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    internal static async Task<RecoveryControlHealth> ReadHealthAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration,RecoverySigningTrust trust,DateTimeOffset now,CancellationToken ct)
    {
        if(now.Offset!=TimeSpan.Zero) throw new RecoveryWorkerException("control_clock_invalid");
        for(var attempt=0;attempt<2;attempt++)
        {
            var marker=await RecoveryVerificationPublisher.ReadOwnerMarkerAsync(dynamo,configuration,
                configuration.Required("OwnerId"),configuration.Required("LibraryId"),ct);
            if(marker is null) return new("unavailable",null,null);
            if(Text(marker,"ownerId")!=configuration.Required("OwnerId") || Text(marker,"libraryId")!=configuration.Required("LibraryId") ||
                Text(marker,"safeStatus")!="healthy" || !Guid.TryParseExact(Text(marker,"pointId"),"D",out var pointId) || pointId==Guid.Empty)
                throw new RecoveryWorkerException("control_owner_marker_invalid");
            var catalog=await ReadPointAsync(dynamo,configuration,trust,pointId,ct);
            if(catalog is null) throw new RecoveryWorkerException("control_verified_point_missing");
            var after=await RecoveryVerificationPublisher.ReadOwnerMarkerAsync(dynamo,configuration,
                configuration.Required("OwnerId"),configuration.Required("LibraryId"),ct);
            if(after is null || Text(after,"pointId")!=Text(marker,"pointId") || Text(after,"payloadSha256")!=Text(marker,"payloadSha256")) continue;
            if(Text(marker,"fullManifestSha256")!=catalog.FullManifestSha256 ||
                Text(marker,"verifiedAtUtc")!=catalog.VerifiedAtUtc.ToString("O",CultureInfo.InvariantCulture))
                throw new RecoveryWorkerException("control_owner_marker_invalid");
            var age=Math.Max(0,(now-catalog.SnapshotCutoffUtc).TotalSeconds);
            return new(age>=45*60 ? "overdue" : "verified",SafePoint(catalog),age);
        }
        throw new RecoveryWorkerException("control_owner_head_unstable");
    }

    internal static async Task<RecoveryControlPage> ReadPageAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration,RecoverySigningTrust trust,int limit,Guid? after,CancellationToken ct)
    {
        if(limit is <1 or >100 || after==Guid.Empty) throw new RecoveryWorkerException("control_point_query_invalid");
        var owner=configuration.Required("OwnerId");var library=configuration.Required("LibraryId");
        var partition=RecoveryOwnerPointIndex.PartitionKey(owner,library);
        Dictionary<string,AttributeValue>? cursor=null;
        if(after is { } afterId)
        {
            cursor=new() { ["pk"]=S(partition),["sk"]=S(RecoveryOwnerPointIndex.PointSortKey(afterId)) };
            var member=await GetAsync(dynamo,configuration.Required("ControlTableName"),cursor,ct);
            if(member is null || Text(member,"ownerId")!=owner || Text(member,"libraryId")!=library) return new([],null);
        }
        var response=await dynamo.QueryAsync(new QueryRequest
        {
            TableName=configuration.Required("ControlTableName"),ConsistentRead=true,Limit=limit,
            KeyConditionExpression="pk=:owner AND begins_with(sk,:points)",
            ExpressionAttributeValues=new() { [":owner"]=S(partition),[":points"]=S("POINT#") },ExclusiveStartKey=cursor
        },ct);
        if(response.Items is null || response.Items.Count>limit) throw new RecoveryWorkerException("control_point_page_invalid");
        var rows=new List<RecoveryControlPoint>();Guid? last=null;
        foreach(var member in response.Items)
        {
            if(Text(member,"pk")!=partition || Text(member,"ownerId")!=owner || Text(member,"libraryId")!=library ||
                !Guid.TryParseExact(Text(member,"pointId"),"D",out var pointId) || pointId==Guid.Empty ||
                Text(member,"sk")!=RecoveryOwnerPointIndex.PointSortKey(pointId))
                throw new RecoveryWorkerException("control_point_member_invalid");
            var catalog=await ReadPointAsync(dynamo,configuration,trust,pointId,ct);
            rows.Add(catalog is null ? new(pointId,"unverified",null,null,null,null,[]) : SafePoint(catalog));last=pointId;
        }
        if(response.LastEvaluatedKey is { Count:>0 } &&
            (last is null || Text(response.LastEvaluatedKey,"pk")!=partition ||
                Text(response.LastEvaluatedKey,"sk")!=RecoveryOwnerPointIndex.PointSortKey(last.Value)))
            throw new RecoveryWorkerException("control_point_page_invalid");
        return new(rows,response.LastEvaluatedKey is { Count:>0 } ? last : null);
    }

    internal static async Task<RecoveryCatalogPayloadV1?> ReadPointAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration,RecoverySigningTrust trust,Guid pointId,CancellationToken ct)
    {
        var envelope = await ReadCatalogEnvelopeAsync(dynamo, configuration, trust, pointId, ct);
        return envelope is null ? null : RecoveryCatalogCodec.Verify(envelope, trust).Payload;
    }

    internal static async Task<RecoveryCatalogEnvelopeV1?> ReadCatalogEnvelopeAsync(IAmazonDynamoDB dynamo,
        RecoveryWorkerConfiguration configuration,RecoverySigningTrust trust,Guid pointId,CancellationToken ct)
    {
        var key=new Dictionary<string,AttributeValue> { ["pk"]=S("POINT#"+pointId.ToString("D")),["sk"]=S("VERIFY") };
        var item=await GetAsync(dynamo,configuration.Required("VerificationTableName"),key,ct);
        if(item is null) return null;
        if(Text(item,"pk")!=key["pk"].S || Text(item,"sk")!="VERIFY") throw new RecoveryWorkerException("control_point_binding_invalid");
        var (catalog,messages)=RecoveryVerificationPublisher.ReadSignedOutbox(item,trust);
        var now=DateTimeOffset.UtcNow;
        if(catalog.RecoveryPointId!=pointId || catalog.OwnerId!=configuration.Required("OwnerId") ||
            catalog.LibraryId!=configuration.Required("LibraryId") || catalog.RecoveryAccountId!=configuration.RequiredAccountId("DestinationAccountId") ||
            catalog.RecoveryRegion!=configuration.RequiredRegion("DestinationRegion") || catalog.SourceAccountId!=configuration.RequiredAccountId("SourceAccountId") ||
            catalog.SourceRegion!=configuration.RequiredRegion("SourceRegion") || catalog.CapturedAtUtc>now ||
            catalog.SnapshotCutoffUtc>now || catalog.VerifiedAtUtc>now)
            throw new RecoveryWorkerException("control_point_binding_invalid");
        return RecoveryCatalogCodec.ParseEnvelope(Encoding.UTF8.GetBytes(messages[0]));
    }
    private static RecoveryControlPoint SafePoint(RecoveryCatalogPayloadV1 catalog)=>new(catalog.RecoveryPointId,"verified",
        catalog.CapturedAtUtc,catalog.SnapshotCutoffUtc,catalog.VerifiedAtUtc,catalog.FullManifestSha256,catalog.Coverage);
    private static async Task<Dictionary<string,AttributeValue>?> GetAsync(IAmazonDynamoDB dynamo,string table,
        Dictionary<string,AttributeValue> key,CancellationToken ct)
    {
        var response=await dynamo.GetItemAsync(new GetItemRequest { TableName=table,Key=key,ConsistentRead=true },ct);
        return response.Item is { Count:>0 } ? response.Item : null;
    }
    private static string? Text(IReadOnlyDictionary<string,AttributeValue> item,string key)=>item.TryGetValue(key,out var value) ? value.S : null;
    private static AttributeValue S(string value)=>new() { S=value };
}
