using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryMaterializerChecks
{
    internal static async Task RunAsync()
    {
        var settings = new Dictionary<string, string>
        {
            ["Environment"] = "materializer-fixture", ["RecoveryWorkerBuildDigest"] = new string('a',64),
            ["TargetBucket"] = "isolated-target", ["TargetKmsKeyId"] = "target-kms",
            ["DestinationAccountId"] = "444455556666", ["DestinationRegion"] = "ap-southeast-2",
            ["DestinationBucket"] = "recovery-payload", ["EvidenceBucket"] = "recovery-evidence",
            ["SourceBucket"] = "primary-media"
        };
        var old = settings.Keys.ToDictionary(key => key, key => Environment.GetEnvironmentVariable("Recovery__"+key));
        foreach (var (key,value) in settings) Environment.SetEnvironmentVariable("Recovery__"+key,value);
        try
        {
            var configuration = new RecoveryWorkerConfiguration("drill");
            var bytes = new byte[] { 1,2,3,4 };
            var sha = RecoveryEvidenceCodec.Hash(bytes);
            var pointId = Guid.NewGuid();
            var reference = new RecoveryObjectReference(RecoveryDataClass.OriginalImage, "owner", "library",
                "original", "source-v1", sha, bytes.Length, [new("owner","library",Guid.NewGuid(),null)]);
            var now = DateTimeOffset.UtcNow;
            var manifest = new RecoveryPointManifestV1(1, Guid.NewGuid(), pointId,"owner","library","ap-southeast-1",
                now,now,"fixture-snapshot","0/1",new string('a',64),new(0,null),[],[reference],[]);
            var pointer = new CopyPointer(pointId,manifest.CaptureId,"owner","library","111122223333","ap-southeast-1",
                "444455556666","ap-southeast-2","primary-capture","manifest","v1",sha,"recovery-evidence",
                "manifest","v1",sha,"checkpoint","v1",sha,1,now,"access_incomplete");
            var receipt = new PrivateCopyReceiptV1("primary-media","original","source-v1",sha,bytes.Length,
                "recovery-payload","payload/sha256/"+sha,"payload-v1","OriginalImage",now);
            var point = new VerifiedCopiedPoint(pointer,manifest,null,null!,[receipt],sha,sha,null);
            using var dynamo = new IntentFake();
            using var s3 = new ObjectFake(bytes);
            var store = new DynamoFenceIntentStore(dynamo,"fences","intents");
            var runId = Guid.NewGuid();
            var result = await RecoveryMaterializer.MaterializeAsync(s3,configuration,point,store,runId,CancellationToken.None);
            Require(result.Count==1 && result[0].DestinationVersionId=="target-v1" && s3.Puts==1,
                "exact_materialization_missing");
            Require(dynamo.Items.Values.All(item => item["state"].S=="settled"), "intents_not_settled");
            await RecoveryMaterializer.MaterializeAsync(s3,configuration,point,store,runId,CancellationToken.None);
            Require(s3.Puts==1, "settled_retry_rewrote_object");
            using var fenced = new IntentFake { Fenced = true };
            await Reject(() => RecoveryMaterializer.MaterializeAsync(s3,configuration,point,
                new(fenced,"fences","intents"),Guid.NewGuid(),CancellationToken.None),"purge_fence_present");
            Require(s3.Puts==1,"fenced_write_reached_provider");
            using var uncertain = new IntentFake();
            using var wrongVersion = new ObjectFake(bytes) { WrongTargetVersion = true };
            await Reject(() => RecoveryMaterializer.MaterializeAsync(wrongVersion,configuration,point,
                new(uncertain,"fences","intents"),Guid.NewGuid(),CancellationToken.None),"artifact_identity_mismatch");
            Require(uncertain.Items.Values.All(item => item["state"].S=="unknown"),"uncertain_write_not_journaled");
            Environment.SetEnvironmentVariable("Recovery__TargetBucket","primary-media");
            await Reject(() => RecoveryMaterializer.MaterializeAsync(s3,configuration,point,store,Guid.NewGuid(),CancellationToken.None),
                "materialization_target_not_isolated");
        }
        finally { foreach(var (key,value) in old) Environment.SetEnvironmentVariable("Recovery__"+key,value); }
    }

    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
    private static async Task Reject(Func<Task> action,string code)
    {
        try { await action(); }
        catch(RecoveryWorkerException exception) when(exception.SafeCode==code) { return; }
        throw new InvalidOperationException("Expected "+code);
    }

    private sealed class IntentFake() : AmazonDynamoDBClient(new AnonymousAWSCredentials(),
        new AmazonDynamoDBConfig { ServiceURL="http://127.0.0.1:1" })
    {
        internal bool Fenced { get; init; }
        internal Dictionary<string,Dictionary<string,AttributeValue>> Items { get; } = [];
        private static string Id(Dictionary<string,AttributeValue> key) => key["scopeId"].S+":"+key["recordId"].S;
        public override Task<GetItemResponse> GetItemAsync(GetItemRequest request,CancellationToken cancellationToken=default) =>
            Task.FromResult(new GetItemResponse { Item = request.TableName=="fences"
                ? Fenced ? new() { ["recordId"]=new() { S="FENCE" } } : []
                : Items.TryGetValue(Id(request.Key),out var item) ? item : [] });
        public override Task<TransactWriteItemsResponse> TransactWriteItemsAsync(TransactWriteItemsRequest request,
            CancellationToken cancellationToken=default)
        {
            var put=request.TransactItems.Single(item=>item.Put is not null).Put;
            if(Fenced || Items.ContainsKey(Id(put.Item))) throw new TransactionCanceledException("fixture condition failed");
            Items.Add(Id(put.Item),put.Item);
            return Task.FromResult(new TransactWriteItemsResponse());
        }
        public override Task<UpdateItemResponse> UpdateItemAsync(UpdateItemRequest request,CancellationToken cancellationToken=default)
        {
            var item=Items[Id(request.Key)];
            item["state"]=request.ExpressionAttributeValues[":state"];
            if(request.ExpressionAttributeValues.TryGetValue(":version",out var version)) item["destinationVersionId"]=version;
            return Task.FromResult(new UpdateItemResponse());
        }
    }

    private sealed class ObjectFake(byte[] bytes) : AmazonS3Client(new AnonymousAWSCredentials(),
        new AmazonS3Config { ServiceURL="http://127.0.0.1:1",ForcePathStyle=true })
    {
        internal int Puts { get; private set; }
        internal bool WrongTargetVersion { get; init; }
        public override Task<GetBucketVersioningResponse> GetBucketVersioningAsync(GetBucketVersioningRequest request,
            CancellationToken cancellationToken=default) => Task.FromResult(new GetBucketVersioningResponse
            { VersioningConfig=new() { Status=VersionStatus.Enabled } });
        public override Task<GetBucketLocationResponse> GetBucketLocationAsync(GetBucketLocationRequest request,
            CancellationToken cancellationToken=default) => Task.FromResult(new GetBucketLocationResponse { Location=S3Region.APSoutheast2 });
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request,CancellationToken cancellationToken=default) =>
            Task.FromResult(new GetObjectResponse { VersionId=WrongTargetVersion && request.BucketName=="isolated-target"
                ? "substituted" : request.VersionId,ContentLength=bytes.Length,ResponseStream=new MemoryStream(bytes) });
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request,CancellationToken cancellationToken=default)
        {
            Require(request.IfNoneMatch=="*" && request.ServerSideEncryptionKeyManagementServiceKeyId=="target-kms" &&
                request.ExpectedBucketOwner=="444455556666","target_write_binding_invalid");
            using var buffer=new MemoryStream();
            await request.InputStream.CopyToAsync(buffer,cancellationToken);
            Require(buffer.ToArray().AsSpan().SequenceEqual(bytes),"target_bytes_changed");
            Puts++;
            return new() { VersionId="target-v1" };
        }
    }
}
