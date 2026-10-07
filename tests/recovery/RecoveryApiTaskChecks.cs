using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.Runtime;
using Platform.Recovery.Worker;
using Task = System.Threading.Tasks.Task;

internal static class RecoveryApiTaskChecks
{
    internal static async Task RunAsync()
    {
        const string arnPrefix = "arn:aws:ecs:ap-southeast-2:444455556666:";
        var settings = new Dictionary<string,string>
        {
            ["Environment"]="api-task-fixture",["RecoveryWorkerBuildDigest"]=new string('a',64),
            ["RecoveryOnly"]="true",["DestinationAccountId"]="444455556666",["DestinationRegion"]="ap-southeast-2",
            ["ApiTaskDefinitionArn"]=arnPrefix+"task-definition/recovery-api:1",["ApiClusterArn"]=arnPrefix+"cluster/recovery",
            ["RunnerSubnetIds"]="subnet-aabb",["ApiSecurityGroupId"]="sg-aabb",["ApiImageDigest"]=new string('b',64)
        };
        var old=settings.Keys.ToDictionary(key=>key,key=>Environment.GetEnvironmentVariable("Recovery__"+key));
        foreach(var (key,value) in settings) Environment.SetEnvironmentVariable("Recovery__"+key,value);
        try
        {
            var configuration=new RecoveryWorkerConfiguration("drill");
            using var ecs=new EcsFake();
            var started=await RecoveryApiTaskRunner.StartAsync(ecs,configuration,Guid.NewGuid(),"recovery_drill_fixture",default);
            Require(started.Endpoint==new Uri("http://10.0.1.2:8080/"),"private_endpoint_missing");
            var request=ecs.Request!;
            Require(request.TaskDefinition==settings["ApiTaskDefinitionArn"] && request.EnableExecuteCommand==false &&
                request.NetworkConfiguration.AwsvpcConfiguration.AssignPublicIp==AssignPublicIp.DISABLED &&
                request.Overrides.ContainerOverrides.Single().Environment is { Count:1 } &&
                request.Overrides.ContainerOverrides.Single().Environment.Single().Name=="Ingest__Database__Name",
                "launch_override_boundary_changed");
            await RecoveryApiTaskRunner.StopAsync(ecs,configuration,started.TaskArn,default);
            Require(ecs.Stops==1,"stop_not_confirmed");
            foreach(var fault in new[] { "image","public","subnet","definition","exec","partial_launch" })
            {
                using var bad=new EcsFake { Fault=fault };
                try { await RecoveryApiTaskRunner.StartAsync(bad,configuration,Guid.NewGuid(),"recovery_drill_fixture",default); }
                catch(RecoveryWorkerException) { Require(bad.Stops==1,"failed_launch_not_stopped"); continue; }
                throw new InvalidOperationException("Accepted untrusted task "+fault);
            }
            using var untouched=new EcsFake();
            try { await RecoveryApiTaskRunner.StartAsync(untouched,configuration,Guid.NewGuid(),"platform_recovery",default); }
            catch(RecoveryWorkerException) { Require(untouched.Request is null,"nonclone_launch_reached_provider"); return; }
            throw new InvalidOperationException("Accepted a non-clone database");
        }
        finally { foreach(var (key,value) in old) Environment.SetEnvironmentVariable("Recovery__"+key,value); }
    }

    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
    private sealed class EcsFake() : AmazonECSClient(new AnonymousAWSCredentials(),
        new AmazonECSConfig { ServiceURL="http://127.0.0.1:1" })
    {
        private const string TaskArn="arn:aws:ecs:ap-southeast-2:444455556666:task/recovery/fixture";
        internal string? Fault { get; init; }
        internal RunTaskRequest? Request { get; private set; }
        internal int Stops { get; private set; }
        public override Task<RunTaskResponse> RunTaskAsync(RunTaskRequest request,CancellationToken cancellationToken=default)
        {
            Request=request;
            return Task.FromResult(new RunTaskResponse { Tasks=[new() { TaskArn=TaskArn }],
                Failures=Fault=="partial_launch" ? [new() { Reason="fixture_failure" }] : [] });
        }
        public override Task<DescribeTasksResponse> DescribeTasksAsync(DescribeTasksRequest request,CancellationToken cancellationToken=default) =>
            Task.FromResult(new DescribeTasksResponse { Tasks=[new()
            {
                TaskArn=TaskArn,ClusterArn=Request!.Cluster,TaskDefinitionArn=Fault=="definition" ? "foreign" : Request.TaskDefinition,
                StartedBy=Request.StartedBy,EnableExecuteCommand=Fault=="exec",LastStatus=Stops>0 ? "STOPPED" : "RUNNING",
                Containers=[new() { Name="api",Image="recovery-api@sha256:"+new string('b',64),
                    ImageDigest="sha256:"+new string(Fault=="image" ? 'c' : 'b',64) }],
                Attachments=[new() { Type="ElasticNetworkInterface",Details=[
                    new() { Name="subnetId",Value=Fault=="subnet" ? "subnet-cccc" : "subnet-aabb" },
                    new() { Name="privateIPv4Address",Value=Fault=="public" ? "8.8.8.8" : "10.0.1.2" }] }]
            }] });
        public override Task<StopTaskResponse> StopTaskAsync(StopTaskRequest request,CancellationToken cancellationToken=default)
        {
            Require(request.Task==TaskArn && request.Cluster==Request!.Cluster,"stop_scope_changed");
            Stops++;
            return Task.FromResult(new StopTaskResponse());
        }
    }
}
