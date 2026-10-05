using System.Reflection;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Api.Features.Recovery;

internal static class RecoveryQueueWorkerChecks
{
    public static async Task RunAsync()
    {
        var sqs = DispatchProxy.Create<IAmazonSQS, EmptyPollSqs>();
        var proxy = (EmptyPollSqs)(object)sqs;
        proxy.EmptyPoll = new ReceiveMessageResponse { Messages = null! };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Recovery:ObservationQueueUrl"] = "https://sqs.invalid/recovery-observations",
        }).Build();
        var worker = new RecoveryObservationQueueWorker(sqs, new RecoveryObservationImporter(null!, configuration), configuration,
            NullLogger<RecoveryObservationQueueWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await proxy.SecondPollStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(2));
        if (proxy.ReceiveCount != 2 || proxy.DeleteCount != 0)
            throw new InvalidOperationException("An empty SQS poll must be ignored before cancellation without deleting a message.");
    }

    public class EmptyPollSqs : DispatchProxy
    {
        public ReceiveMessageResponse EmptyPoll { get; set; } = null!;
        public int ReceiveCount { get; private set; }
        public int DeleteCount { get; private set; }
        public TaskCompletionSource SecondPollStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IAmazonSQS.ReceiveMessageAsync) && targetMethod.ReturnType == typeof(Task<ReceiveMessageResponse>))
            {
                ReceiveCount++;
                if (ReceiveCount == 1) return Task.FromResult(EmptyPoll);
                SecondPollStarted.TrySetResult();
                return WaitForCancellation((CancellationToken)args![1]!);
            }
            if (targetMethod?.Name == nameof(IAmazonSQS.DeleteMessageAsync) && targetMethod.ReturnType == typeof(Task<DeleteMessageResponse>))
            {
                DeleteCount++;
                return Task.FromResult(new DeleteMessageResponse());
            }
            throw new NotSupportedException($"Unexpected SQS method: {targetMethod?.Name ?? "unknown"}");
        }

        private static async Task<ReceiveMessageResponse> WaitForCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ReceiveMessageResponse();
        }
    }
}
