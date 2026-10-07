using System.Net;
using System.Text;
using Platform.Recovery;
using Platform.Recovery.Worker;

internal static class RecoveryOwnerAccessProbeChecks
{
    internal static async Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var mapping = new RecoveryIdentityApprovalV1(1,Guid.NewGuid(),"https://pool.example/issuer","client",
            "owner-subject","preserved-owner","owner-mfa","owner-key",now);
        var api = new RecoveryApiTask("fixed-task",new("http://10.0.1.2:8080/"));
        string Token(string subject) => "header."+Convert.ToBase64String(RecoveryEvidenceCodec.SerializeCanonical(new
        {
            sub=subject,iss=mapping.Issuer,aud=mapping.Audience,token_use="id",
            auth_time=now.AddMinutes(-1).ToUnixTimeSeconds(),exp=now.AddMinutes(10).ToUnixTimeSeconds()
        })).TrimEnd('=').Replace('+','-').Replace('/','_')+".signature";
        var ownerToken=Token(mapping.Subject);
        var foreignToken=Token("foreign-subject");
        using var handler=new ProbeHandler();
        using var client=new HttpClient(handler);
        var result=await RecoveryOwnerAccessProbe.RunAsync(client,api,Guid.NewGuid(),mapping,ownerToken,foreignToken,default);
        Require(result.OwnerHttpStatus==200 && result.ForeignHttpStatus==403 && handler.Calls==2,"probe_results_missing");
        var bytes=Encoding.UTF8.GetString(RecoveryEvidenceCodec.SerializeCanonical(result));
        Require(!bytes.Contains(ownerToken,StringComparison.Ordinal) && !bytes.Contains(mapping.Subject,StringComparison.Ordinal),
            "probe_retained_credentials_or_subject");
        foreach(var status in new[] { 200,401,302 })
        {
            using var bad=new ProbeHandler { ForeignStatus=status };
            using var badClient=new HttpClient(bad);
            await Reject(()=>RecoveryOwnerAccessProbe.RunAsync(badClient,api,Guid.NewGuid(),mapping,ownerToken,foreignToken,default));
        }
        using var untouched=new ProbeHandler();
        using var untouchedClient=new HttpClient(untouched);
        await Reject(()=>RecoveryOwnerAccessProbe.RunAsync(untouchedClient,api with { Endpoint=new("http://8.8.8.8:8080/") },
            Guid.NewGuid(),mapping,ownerToken,foreignToken,default));
        await Reject(()=>RecoveryOwnerAccessProbe.RunAsync(untouchedClient,api,Guid.NewGuid(),mapping,ownerToken,ownerToken,default));
        await Reject(()=>RecoveryOwnerAccessProbe.RunAsync(untouchedClient,api,Guid.NewGuid(),mapping,"invalid",foreignToken,default));
        Require(untouched.Calls==0,"untrusted_probe_reached_endpoint");
    }

    private static void Require(bool value,string code) { if(!value) throw new InvalidOperationException(code); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch(RecoveryWorkerException) { return; }
        throw new InvalidOperationException("Untrusted owner probe accepted");
    }
    private sealed class ProbeHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal int ForeignStatus { get; init; }=403;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Require(request.RequestUri==new Uri("http://10.0.1.2:8080/v1/recovery/health") &&
                request.Headers.Authorization?.Scheme=="Bearer","probe_destination_or_auth_changed");
            Calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)(Calls==1 ? 200 : ForeignStatus))
            { Content=new StringContent("{\"status\":\"unavailable\"}") });
        }
    }
}
