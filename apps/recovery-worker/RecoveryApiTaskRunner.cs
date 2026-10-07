using System.Net;
using Amazon.ECS;
using Amazon.ECS.Model;
using Task = System.Threading.Tasks.Task;

namespace Platform.Recovery.Worker;

internal sealed record RecoveryApiTask(string TaskArn, Uri Endpoint);

/// <summary>Launches only the configured recovery API definition, on isolated subnets,
/// with the clone database name as its sole override. It never creates a task definition.</summary>
internal static class RecoveryApiTaskRunner
{
    internal static async Task<RecoveryApiTask> StartAsync(IAmazonECS ecs,
        RecoveryWorkerConfiguration configuration, Guid runId, string databaseName,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty || configuration.Required("RecoveryOnly") != "true" ||
            !databaseName.StartsWith("recovery_drill_", StringComparison.Ordinal) || databaseName.Length > 63 ||
            databaseName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new RecoveryWorkerException("api_candidate_binding_invalid");
        var account = configuration.RequiredAccountId("DestinationAccountId");
        var region = configuration.RequiredRegion("DestinationRegion");
        var definition = configuration.Required("ApiTaskDefinitionArn");
        var cluster = configuration.Required("ApiClusterArn");
        RequireArn(definition, account, region, "task-definition/");
        RequireArn(cluster, account, region, "cluster/");
        var subnets = configuration.Required("RunnerSubnetIds").Split(',');
        if (subnets.Length is < 1 or > 16 || subnets.Distinct().Count() != subnets.Length ||
            subnets.Any(subnet => !ValidResourceId(subnet, "subnet-")) ||
            !ValidResourceId(configuration.Required("ApiSecurityGroupId"), "sg-"))
            throw new RecoveryWorkerException("api_candidate_network_invalid");
        var imageDigest = configuration.RequiredHash("ApiImageDigest");
        var response = await ecs.RunTaskAsync(new RunTaskRequest
        {
            Cluster = cluster, TaskDefinition = definition, Count = 1, LaunchType = LaunchType.FARGATE,
            EnableExecuteCommand = false, StartedBy = runId.ToString("D"), ClientToken = runId.ToString("D"),
            NetworkConfiguration = new()
            {
                AwsvpcConfiguration = new()
                {
                    AssignPublicIp = AssignPublicIp.DISABLED, Subnets = subnets.ToList(),
                    SecurityGroups = [configuration.Required("ApiSecurityGroupId")]
                }
            },
            Overrides = new()
            {
                ContainerOverrides = [new()
                {
                    Name = "api", Environment = [new() { Name = "Ingest__Database__Name", Value = databaseName }]
                }]
            }
        }, cancellationToken);
        if (response.Failures is { Count: > 0 } || response.Tasks is not { Count: 1 } ||
            string.IsNullOrWhiteSpace(response.Tasks[0].TaskArn))
        {
            // A partial launch response can contain both failures and running tasks.
            // Confirm cleanup before allowing the caller to discard the clone.
            foreach (var launched in response.Tasks ?? [])
                RequireArn(launched.TaskArn, account, region, "task/");
            foreach (var launched in response.Tasks ?? [])
                await StopAsync(ecs, configuration, launched.TaskArn, CancellationToken.None);
            throw new RecoveryWorkerException("api_candidate_launch_failed");
        }
        var taskArn = response.Tasks[0].TaskArn;
        RequireArn(taskArn, account, region, "task/");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(5));
            while (true)
            {
                var described = await ecs.DescribeTasksAsync(new DescribeTasksRequest
                { Cluster = cluster, Tasks = [taskArn] }, deadline.Token);
                if (described.Failures is { Count: > 0 } || described.Tasks is not { Count: 1 })
                    throw new RecoveryWorkerException("api_candidate_task_missing");
                var task = described.Tasks[0];
                if (task.TaskArn != taskArn || task.ClusterArn != cluster || task.TaskDefinitionArn != definition ||
                    task.StartedBy != runId.ToString("D") || task.EnableExecuteCommand == true)
                    throw new RecoveryWorkerException("api_candidate_runtime_binding_mismatch");
                if (task.LastStatus == "STOPPED") throw new RecoveryWorkerException("api_candidate_stopped_before_ready");
                if (task.LastStatus == "RUNNING")
                {
                    var api = task.Containers?.SingleOrDefault(container => container.Name == "api");
                    if (task.Containers is not { Count: 1 } || api?.ImageDigest != "sha256:" + imageDigest ||
                        api.Image?.EndsWith("@sha256:" + imageDigest, StringComparison.Ordinal) != true)
                        throw new RecoveryWorkerException("api_candidate_image_mismatch");
                    var eni = task.Attachments?.SingleOrDefault(attachment => attachment.Type == "ElasticNetworkInterface");
                    var subnet = eni?.Details?.SingleOrDefault(detail => detail.Name == "subnetId")?.Value;
                    var address = eni?.Details?.SingleOrDefault(detail => detail.Name == "privateIPv4Address")?.Value;
                    if (subnet is null || !subnets.Contains(subnet, StringComparer.Ordinal) ||
                        !IPAddress.TryParse(address, out var ip) || !IsPrivateIpv4(ip))
                        throw new RecoveryWorkerException("api_candidate_endpoint_not_isolated");
                    return new(taskArn, new Uri("http://" + ip + ":8080/"));
                }
                await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
            }
        }
        catch
        {
            await StopAsync(ecs, configuration, taskArn, CancellationToken.None);
            throw;
        }
    }

    internal static async Task StopAsync(IAmazonECS ecs, RecoveryWorkerConfiguration configuration,
        string taskArn, CancellationToken cancellationToken)
    {
        RequireArn(taskArn, configuration.RequiredAccountId("DestinationAccountId"),
            configuration.RequiredRegion("DestinationRegion"), "task/");
        var cluster = configuration.Required("ApiClusterArn");
        await ecs.StopTaskAsync(new StopTaskRequest
        { Cluster = cluster, Task = taskArn, Reason = "recovery_candidate_cleanup" }, cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        while (true)
        {
            var response = await ecs.DescribeTasksAsync(new DescribeTasksRequest
            { Cluster = cluster, Tasks = [taskArn] }, deadline.Token);
            if (response.Failures is { Count: > 0 } || response.Tasks is not { Count: 1 } ||
                response.Tasks[0].TaskArn != taskArn || response.Tasks[0].ClusterArn != cluster)
                throw new RecoveryWorkerException("api_candidate_stop_unconfirmed");
            if (response.Tasks[0].LastStatus == "STOPPED") return;
            await Task.Delay(TimeSpan.FromSeconds(2), deadline.Token);
        }
    }

    private static bool ValidResourceId(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length > prefix.Length && value[prefix.Length..].All(char.IsAsciiHexDigit);

    private static void RequireArn(string value, string account, string region, string resourcePrefix)
    {
        var prefix = "arn:aws:ecs:" + region + ":" + account + ":" + resourcePrefix;
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length <= prefix.Length ||
            value[prefix.Length..].Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('/' or '-' or '_' or ':')))
            throw new RecoveryWorkerException("api_candidate_arn_invalid");
    }

    internal static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
            bytes[0] == 192 && bytes[1] == 168);
    }
}
