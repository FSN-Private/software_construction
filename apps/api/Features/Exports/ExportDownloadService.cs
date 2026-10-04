using Amazon.S3;
using Amazon.S3.Model;
using Npgsql;
using Platform.Contracts.Exports;
using System.Security.Cryptography;
using System.Text;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public sealed class ExportDownloadService(NpgsqlDataSource source, IAmazonS3 storage, IConfiguration config)
{
    private const string Cookie = "__Secure-platform-export";
    private static string Hash(string value) => ExportIntegrity.Hash(Encoding.UTF8.GetBytes(value));
    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string SessionHash(HttpContext c) => Hash(string.Join('\n', c.User.FindFirst("iss")?.Value, c.User.FindFirst("sub")?.Value, c.User.FindFirst("origin_jti")?.Value ?? c.User.FindFirst("auth_time")?.Value ?? c.Request.Headers.Authorization.ToString()));
    private string Origin => Uri.TryCreate(config["Exports:DownloadOrigin"], UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) ? uri.GetLeftPart(UriPartial.Authority) : throw new ExportProblem("download_unavailable", 503);
    private static string Path(string library, Guid id) => $"/v1/libraries/{Uri.EscapeDataString(library)}/exports/requests/{id:D}";
    public async Task<object> MintAsync(HttpContext c, string owner, string library, Guid id, CancellationToken ct)
    {
        var origin = Origin;
        if (!long.TryParse(c.User.FindFirst("exp")?.Value, out var exp)) throw new ExportProblem("unauthenticated", 401);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(exp); if (expiry <= DateTimeOffset.UtcNow) throw new ExportProblem("unauthenticated", 401);
        var bearer = c.Request.Headers.Authorization.ToString(); if (!bearer.StartsWith("Bearer ", StringComparison.Ordinal)) throw new ExportProblem("unauthenticated", 401);
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        await ReadyAsync(db, owner, library, id, ct);
        var session = await ScalarAsync(db, "INSERT INTO export_download_sessions(id,owner_id,session_hash,expires_at) VALUES($1,$2,$3,$4) ON CONFLICT(owner_id,session_hash) DO UPDATE SET expires_at=GREATEST(export_download_sessions.expires_at,excluded.expires_at) RETURNING id", ct, Guid.NewGuid(), owner, SessionHash(c), expiry);
        if (await ScalarAsync(db, "SELECT 1 FROM export_download_sessions WHERE id=$1 AND revoked_at IS NULL AND expires_at>now()", ct, session) is null) throw new ExportProblem("unauthenticated", 401);
        var code = Token(); var until = new[] { expiry, DateTimeOffset.UtcNow.AddSeconds(60) }.Min();
        await ExecuteAsync(db, "INSERT INTO export_download_grants(token_hash,request_id,session_id,kind,expires_at,bearer_expires_at) VALUES($1,$2,$3,'bootstrap',$4,$5)", ct, Hash(code), id, session, until, expiry);
        await AuditAsync(db, owner, c.Items["platform.operation_id"]?.ToString() ?? ExportDatabase.OperationId, "exports.download_granted", id, "accepted", ct); await tx.CommitAsync(ct);
        c.Items["platform.idempotency.reissue"] = true;
        return new { Code = code, ExchangeUrl = origin + Path(library, id) + "/download-session", ExpiresAtUtc = until };
    }
    public async Task ExchangeAsync(HttpContext c, string library, Guid id, CancellationToken ct)
    {
        RequireOrigin(c);
        if (c.Request.Headers.Origin.ToString() != config["Exports:WebOrigin"] || !c.Request.HasFormContentType || c.Request.ContentLength is null or > 4096) throw new ExportProblem("download_denied", 403);
        var form = await c.Request.ReadFormAsync(ct); var code = form["code"].ToString(); if (code.Length != 64) throw new ExportProblem("download_denied", 403);
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct);
        var grant = await LookupAsync(db, Hash(code), id, library, "bootstrap", ct);
        c.Items["exports.audit_owner"] = grant.Owner;
        await ExportDatabase.LockAsync(db, grant.Owner, library, ct); await ReadyAsync(db, grant.Owner, library, id, ct);
        if (await ExecuteAsync(db, "UPDATE export_download_grants SET consumed_at=now() WHERE token_hash=$1 AND consumed_at IS NULL AND revoked_at IS NULL AND expires_at>now()", ct, Hash(code)) != 1) throw new ExportProblem("download_denied", 403);
        var cookie = Token(); var until = new[] { grant.SessionExpiry, DateTimeOffset.UtcNow.AddMinutes(5) }.Min();
        await ExecuteAsync(db, "INSERT INTO export_download_grants(token_hash,request_id,session_id,kind,expires_at,bearer_expires_at) VALUES($1,$2,$3,'cookie',$4,$5)", ct, Hash(cookie), id, grant.Session, until, grant.SessionExpiry);
        await AuditAsync(db, grant.Owner, ExportDatabase.OperationId, "exports.download_session_started", id, "accepted", ct);
        await tx.CommitAsync(ct);
        c.Response.Cookies.Append(Cookie, cookie, new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = Path(library, id) + "/download", Expires = until, IsEssential = true });
        c.Response.StatusCode = 303; c.Response.Headers.Location = Path(library, id) + "/download";
    }
    public async Task RevokeAsync(HttpContext c, string owner, CancellationToken ct)
    {
        var expiry = BearerExpiry(c);
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await LockOwnerAsync(db, owner, ct);
        await ExecuteAsync(db, "INSERT INTO export_download_sessions(id,owner_id,session_hash,expires_at,revoked_at) VALUES($3,$1,$2,$4,now()) ON CONFLICT(owner_id,session_hash) DO UPDATE SET revoked_at=now(); UPDATE export_download_grants g SET revoked_at=now() FROM export_download_sessions s WHERE g.session_id=s.id AND s.owner_id=$1 AND s.revoked_at IS NOT NULL", ct, owner, SessionHash(c), Guid.NewGuid(), expiry);
        await tx.CommitAsync(ct);
    }
    public async Task StreamAsync(HttpContext c, string library, Guid id, CancellationToken ct)
    {
        Platform.Api.Telemetry.PlatformTelemetry.ExportDownloadAttempts.Add(1);
        RequireOrigin(c);
        await using var db = await source.OpenConnectionAsync(ct);
        string owner; Guid session;
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            if (Platform.Contracts.OwnerAuthorization.TryGetOwner(c.User, out var bearerOwner))
            {
                owner = bearerOwner;
                var bearerExpiry = BearerExpiry(c);
                await LockOwnerAsync(db, owner, ct);
                session = (Guid)(await ScalarAsync(db, "INSERT INTO export_download_sessions(id,owner_id,session_hash,expires_at) VALUES($1,$2,$3,$4) ON CONFLICT(owner_id,session_hash) DO UPDATE SET expires_at=GREATEST(export_download_sessions.expires_at,excluded.expires_at) RETURNING id", ct, Guid.NewGuid(), owner, SessionHash(c), bearerExpiry))!;
            }
            else { var token = c.Request.Cookies[Cookie]; if (token is null || token.Length != 64) throw new ExportProblem("download_denied", 403); var grant = await LookupAsync(db, Hash(token), id, library, "cookie", ct); owner = grant.Owner; session = grant.Session; }
            c.Items["exports.audit_owner"] = owner;
            await ExportDatabase.LockAsync(db, owner, library, ct); await ReadyAsync(db, owner, library, id, ct);
            await RequireSessionAsync(db, session, ct);
            await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.download_admitted", id, "accepted", ct); await tx.CommitAsync(ct);
        }
        string key; string version; string sha; long length;
        await using (var cmd = Command(db, "SELECT package_key,package_version,package_sha256,package_bytes FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3", id, owner, library))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { if (!await r.ReadAsync(ct) || r.IsDBNull(0) || r.IsDBNull(1) || r.IsDBNull(2) || r.IsDBNull(3)) throw new ExportProblem("download_unavailable", 409); key = r.GetString(0); version = r.GetString(1); sha = r.GetString(2).Trim(); length = r.GetInt64(3); }
        var etag = '"' + sha + '"';
        var range = ExportRange.Parse(c.Request.Headers.Range.ToString(), c.Request.Headers.IfRange.ToString(), etag, length);
        c.Response.Headers.CacheControl = "private, no-store"; c.Response.Headers.ETag = etag; c.Response.Headers.AcceptRanges = "bytes";
        c.Response.Headers["X-Content-SHA256"] = sha; c.Response.Headers.ContentDisposition = $"attachment; filename=\"export-{id:N}.zip\"";
        c.Response.ContentType = "application/zip"; c.Response.ContentLength = range.End - range.Start + 1;
        if (range.Partial) { c.Response.StatusCode = 206; c.Response.Headers.ContentRange = $"bytes {range.Start}-{range.End}/{length}"; }
        using var bytes = await storage.GetObjectAsync(new GetObjectRequest { BucketName = config["Rendering:DerivativeBucket"] ?? throw new ExportProblem("download_unavailable", 503), Key = key, VersionId = version, ByteRange = range.Partial ? new ByteRange(range.Start, range.End) : null }, ct);
        if (bytes.ContentLength != c.Response.ContentLength) throw new ExportProblem("integrity_error");
        if (HttpMethods.IsHead(c.Request.Method)) return;
        var buffer = new byte[81920]; long sent = 0; var checkedAt = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                if (DateTimeOffset.UtcNow - checkedAt >= TimeSpan.FromSeconds(1))
                {
                    await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
                    await ReadyAsync(db, owner, library, id, ct);
                    // Credential expiry controls admission; admitted transfers still observe revocation and lifecycle changes.
                    if (await ScalarAsync(db, "SELECT 1 FROM export_download_sessions WHERE id=$1 AND revoked_at IS NULL", ct, session) is null) throw new ExportProblem("download_denied", 403);
                    await tx.CommitAsync(ct); checkedAt = DateTimeOffset.UtcNow;
                }
                var read = await bytes.ResponseStream.ReadAsync(buffer, ct); if (read == 0) break; sent += read;
                if (sent > c.Response.ContentLength) throw new ExportProblem("integrity_error");
                await c.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (sent != c.Response.ContentLength) throw new ExportProblem("integrity_error");
            await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.download_completed", id, "completed", ct);
        }
        catch { c.Abort(); throw; }
    }
    private void RequireOrigin(HttpContext c)
    {
        if (!config.GetValue<bool>("Exports:Enabled")) throw new ExportProblem("exports_disabled", 503);
        var trustedTransport = config["Exports:TransportSecret"] is { Length: >= 32 } secret && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(c.Request.Headers["X-Export-Transport"].ToString()));
        if ((!c.Request.IsHttps && !trustedTransport) || !string.Equals(c.Request.Host.Value, new Uri(Origin).Authority, StringComparison.OrdinalIgnoreCase)) throw new ExportProblem("download_denied", 403);
        c.Response.Headers.CacheControl = "private, no-store"; c.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    private static DateTimeOffset BearerExpiry(HttpContext c)
    {
        if (!long.TryParse(c.User.FindFirst("exp")?.Value, out var exp) || !c.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)) throw new ExportProblem("unauthenticated", 401);
        var expiry = DateTimeOffset.FromUnixTimeSeconds(exp);
        return expiry > DateTimeOffset.UtcNow ? expiry : throw new ExportProblem("unauthenticated", 401);
    }
    private static async Task RequireSessionAsync(NpgsqlConnection db, Guid session, CancellationToken ct)
    {
        if (await ScalarAsync(db, "SELECT 1 FROM export_download_sessions WHERE id=$1 AND revoked_at IS NULL AND expires_at>now()", ct, session) is null) throw new ExportProblem("download_denied", 403);
    }
    private static async Task ReadyAsync(NpgsqlConnection db, string owner, string library, Guid id, CancellationToken ct)
    {
        if (await ScalarAsync(db, "SELECT 1 FROM export_requests WHERE id=$1 AND owner_id=$2 AND library_id=$3 AND state IN ('completed','partial') AND expires_at>now()", ct, id, owner, library) is null) throw new ExportProblem("download_unavailable", 409);
        var snapshot = ExportDatabase.Decode<ExportSnapshot>((string)(await ScalarAsync(db, "SELECT snapshot::text FROM export_requests WHERE id=$1", ct, id))!);
        var pins = new List<ExportPin>();
        await using (var cmd = Command(db, "SELECT item_id FROM export_results WHERE request_id=$1 AND outcome->>'status'='completed'", id))
        await using (var r = await cmd.ExecuteReaderAsync(ct)) { while (await r.ReadAsync(ct)) pins.Add(snapshot.Items.Single(p => p.ItemId == r.GetGuid(0))); }
        var availability = await ExportSelectionService.AvailabilityBatchAsync(db, owner, library, pins, snapshot.PresetId == "originals-v1", ct);
        if (availability.Values.Any(reason => reason is not null)) throw new ExportProblem("download_unavailable", 409);
    }
    private static async Task<(string Owner, Guid Session, DateTimeOffset SessionExpiry)> LookupAsync(NpgsqlConnection db, string hash, Guid id, string library, string kind, CancellationToken ct)
    {
        await using var cmd = Command(db, "SELECT s.owner_id,s.id,LEAST(s.expires_at,g.bearer_expires_at) FROM export_download_grants g JOIN export_download_sessions s ON s.id=g.session_id JOIN export_requests r ON r.id=g.request_id AND r.owner_id=s.owner_id WHERE g.token_hash=$1 AND g.request_id=$2 AND r.library_id=$3 AND g.kind=$4 AND g.revoked_at IS NULL AND g.consumed_at IS NULL AND g.expires_at>now() AND g.bearer_expires_at>now() AND s.revoked_at IS NULL AND s.expires_at>now()", hash, id, library, kind);
        await using var r = await cmd.ExecuteReaderAsync(ct); if (!await r.ReadAsync(ct)) throw new ExportProblem("download_denied", 403); return (r.GetString(0), r.GetGuid(1), r.GetFieldValue<DateTimeOffset>(2));
    }
}
public static class ExportRange
{
    public static (long Start, long End, bool Partial) Parse(string value, string ifRange, string etag, long length)
    {
        if (length < 1) throw new ExportProblem("integrity_error");
        if (string.IsNullOrEmpty(value) || !string.IsNullOrEmpty(ifRange) && ifRange != etag) return (0, length - 1, false);
        if (!value.StartsWith("bytes=", StringComparison.Ordinal) || value.Contains(',')) throw new ExportProblem("invalid_range", 416);
        var split = value[6..].Split('-'); if (split.Length != 2) throw new ExportProblem("invalid_range", 416);
        if (split[0] == "") { if (!long.TryParse(split[1], out var suffix) || suffix < 1) throw new ExportProblem("invalid_range", 416); return (Math.Max(0, length - suffix), length - 1, true); }
        if (!long.TryParse(split[0], out var start) || start < 0 || start >= length || split[1] != "" && (!long.TryParse(split[1], out var parsed) || parsed < start)) throw new ExportProblem("invalid_range", 416);
        var end = split[1] == "" ? length - 1 : Math.Min(long.Parse(split[1]), length - 1); return (start, end, true);
    }
}
