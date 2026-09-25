using Amazon.S3;
using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Contracts.Rendering;
using Platform.Render.Worker;
using System.Text.Json;

var queueUrl = Required("Rendering__QueueUrl");
var sourceBucket = Required("Rendering__SourceBucket");
var derivativeBucket = Required("Rendering__DerivativeBucket");
var workspaceRoot = Required("Rendering__WorkspaceRoot");
var callbackBaseUrl = Required("Rendering__CallbackBaseUrl");
var region = Required("Rendering__AwsRegion");
var rendererDigest = Required("Rendering__DeployedArtifactDigest");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; stop.Cancel(); };
using var sqs = new AmazonSQSClient();
using var s3 = new AmazonS3Client();
var sources = new S3RenderSourceResolver(s3, sourceBucket);
var derivatives = new S3PrivateDerivativeStore(s3, derivativeBucket);
var callbacks = new SigV4RenderCallbackClient(callbackBaseUrl, region);
var executor = new ConstrainedRendererExecutor();
var commands = new DarktableCommandFactory();

while (!stop.IsCancellationRequested)
{
    var received = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest { QueueUrl = queueUrl, MaxNumberOfMessages = 1, WaitTimeSeconds = 20, VisibilityTimeout = 2100 }, stop.Token);
    foreach (var message in received.Messages)
    {
        try
        {
            var payload = JsonSerializer.Deserialize<RenderQueueMessage>(message.Body) ?? throw new InvalidDataException("invalid_render_queue_message");
            payload.Validate();
            if (!string.Equals(payload.Request.RendererArtifactDigest, rendererDigest, StringComparison.Ordinal))
            {
                if (await callbacks.ClaimAsync(payload.RequestId, payload.AttemptId, stop.Token)) await callbacks.FailAsync(payload.RequestId, payload.AttemptId, "incompatible_renderer", stop.Token);
                await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, stop.Token);
                continue;
            }
            var worker = new RenderWorker(new SqsReceiptQueue(sqs, queueUrl, message.ReceiptHandle), sources, commands, executor, derivatives, callbacks, workspaceRoot);
            await worker.ExecuteAsync(new RenderJob(payload.RequestId, payload.AttemptId, payload.Request, payload.Policy, payload), stop.Token);
        }
        catch (InvalidDataException)
        {
            await sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, stop.Token);
        }
    }
}

static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is required.");
