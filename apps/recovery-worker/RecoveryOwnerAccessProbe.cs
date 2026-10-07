using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Platform.Recovery;

namespace Platform.Recovery.Worker;

internal static class RecoveryOwnerAccessProbe
{
    internal static async Task<RecoveryOwnerAccessTranscriptV1> RunAsync(RecoveryApiTask api, Guid probeId,
        RecoveryIdentityApprovalV1 mapping, string ownerToken, string foreignToken, CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return await RunAsync(client, api, probeId, mapping, ownerToken, foreignToken, cancellationToken);
    }

    internal static async Task<RecoveryOwnerAccessTranscriptV1> RunAsync(HttpClient client, RecoveryApiTask api,
        Guid probeId, RecoveryIdentityApprovalV1 mapping, string ownerToken, string foreignToken,
        CancellationToken cancellationToken)
    {
        var endpoint = api.Endpoint;
        if (probeId == Guid.Empty || endpoint.Scheme != "http" || endpoint.Port != 8080 || endpoint.AbsolutePath != "/" ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0 ||
            !IPAddress.TryParse(endpoint.Host, out var address) || !RecoveryApiTaskRunner.IsPrivateIpv4(address))
            throw new RecoveryWorkerException("owner_probe_endpoint_invalid");
        var now = DateTimeOffset.UtcNow;
        var owner = ReadTokenIdentity(ownerToken, mapping, now);
        var foreign = ReadTokenIdentity(foreignToken, mapping, now);
        if (owner.Subject != mapping.Subject || foreign.Subject == owner.Subject)
            throw new RecoveryWorkerException("owner_probe_identity_invalid");
        var ownerAt = DateTimeOffset.UtcNow;
        var ownerResponse = await RequestAsync(client, endpoint, ownerToken, cancellationToken);
        if (ownerResponse.Status != 200) throw new RecoveryWorkerException("owner_probe_owner_denied");
        // A 403 proves the API authenticated the foreign token and rejected its mapping.
        // An invalid/expired token's 401 is insufficient for this probe even though the
        // shared transcript schema also supports other independently attested 401 cases.
        var foreignAt = DateTimeOffset.UtcNow;
        var foreignResponse = await RequestAsync(client, endpoint, foreignToken, cancellationToken);
        if (foreignResponse.Status != 403) throw new RecoveryWorkerException("owner_probe_foreign_not_denied");
        var transcript = new RecoveryOwnerAccessTranscriptV1(1, probeId, owner.LoginAtUtc, ownerAt,
            HashText(owner.Subject), ownerResponse.Status, HashText(mapping.PreservedOwnerId), ownerResponse.Hash,
            foreignAt, HashText(foreign.Subject), foreignResponse.Status, foreignResponse.Hash);
        transcript.Validate(mapping.PreservedOwnerId, DateTimeOffset.UtcNow);
        return transcript;
    }

    private static async Task<(int Status, string Hash)> RequestAsync(HttpClient client, Uri endpoint,
        string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "v1/recovery/health"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new RecoveryWorkerException("owner_probe_redirect_denied");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (body.Length + read > 64 * 1024) throw new RecoveryWorkerException("owner_probe_response_limit");
            body.Write(buffer, 0, read);
        }
        return ((int)response.StatusCode, RecoveryEvidenceCodec.Hash(body.ToArray()));
    }

    private static (string Subject, DateTimeOffset LoginAtUtc) ReadTokenIdentity(string token,
        RecoveryIdentityApprovalV1 mapping, DateTimeOffset nowUtc)
    {
        // Claims are hints until the real API verifies the JWT. They never authorize a
        // mapping or data access here; both authenticated HTTP outcomes are mandatory.
        try
        {
            if (token.Length is < 10 or > 64 * 1024 || token.Any(char.IsWhiteSpace))
                throw new InvalidDataException();
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(string.IsNullOrEmpty)) throw new InvalidDataException();
            var encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(encoded));
            var payload = document.RootElement;
            var subject = payload.GetProperty("sub").GetString();
            var login = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("auth_time").GetInt64());
            var expiry = DateTimeOffset.FromUnixTimeSeconds(payload.GetProperty("exp").GetInt64());
            if (payload.GetProperty("iss").GetString() != mapping.Issuer ||
                payload.GetProperty("aud").GetString() != mapping.Audience ||
                payload.GetProperty("token_use").GetString() != "id" ||
                string.IsNullOrWhiteSpace(subject) || subject.Length > 256 || subject.Any(char.IsControl) ||
                login > nowUtc || login < nowUtc.AddMinutes(-15) || expiry <= nowUtc)
                throw new InvalidDataException();
            return (subject, login);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or FormatException or
            KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            throw new RecoveryWorkerException("owner_probe_token_invalid", exception);
        }
    }

    private static string HashText(string value) => RecoveryEvidenceCodec.Hash(Encoding.UTF8.GetBytes(value));
}
