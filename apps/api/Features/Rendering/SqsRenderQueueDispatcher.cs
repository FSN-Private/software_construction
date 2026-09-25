using Amazon.SQS;
using Amazon.SQS.Model;
using Platform.Contracts.Rendering;
using System.Text.Json;

namespace Platform.Api.Features.Rendering;

public sealed class SqsRenderQueueDispatcher(IAmazonSQS sqs, IConfiguration configuration) : IRenderQueueDispatcher
{
    private readonly string queueUrl = configuration["Rendering:QueueUrl"] ?? throw new InvalidOperationException("Rendering:QueueUrl is required.");

    public Task EnqueueAsync(RenderQueueMessage message, CancellationToken cancellationToken = default)
    {
        message.Validate();
        return sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(message),
        }, cancellationToken);
    }
}
