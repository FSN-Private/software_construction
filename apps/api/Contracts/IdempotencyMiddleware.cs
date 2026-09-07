using Platform.Contracts;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Api.Contracts;

public sealed class IdempotencyMiddleware(RequestDelegate next, IdempotencyStore? store = null)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) && !HttpMethods.IsPatch(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method)) { await next(context); return; }
        if (!RequestContracts.HasIdempotencyKey(context)) { await RequestContracts.Problem(context, StatusCodes.Status400BadRequest, "idempotency_key_required", "Idempotency key required").ExecuteAsync(context); return; }
        if (!OwnerAuthorization.TryGetOwner(context.User, out var ownerId)) { await next(context); return; }
        if (store is null) { await next(context); return; }
        var key = context.Request.Headers[RequestContracts.IdempotencyHeader].First()!;
        var fingerprint = await FingerprintAsync(context);
        try
        {
            var existing = await store.GetAsync(ownerId, key, fingerprint, context.RequestAborted);
            if (existing?.Reissue == true && existing.StatusCode is not null)
            {
                if (!await store.ResetAsync(ownerId, key, context.RequestAborted)) { await RequestContracts.Problem(context, StatusCodes.Status409Conflict, "idempotency_in_progress", "Request is in progress").ExecuteAsync(context); return; }
                existing = null;
            }
            if (existing is { StatusCode: { } status, Response: { } replay }) { context.Response.StatusCode = status; await context.Response.WriteAsync(replay, context.RequestAborted); return; }
            if (existing is not null && !existing.Reissue && !await store.ReleaseStaleAsync(ownerId, key, CancellationToken.None)) { await RequestContracts.Problem(context, StatusCodes.Status409Conflict, "idempotency_in_progress", "Request is in progress").ExecuteAsync(context); return; }
            if (!await store.ReserveAsync(ownerId, key, fingerprint, context.RequestAborted)) { await RequestContracts.Problem(context, StatusCodes.Status409Conflict, "idempotency_in_progress", "Request is in progress").ExecuteAsync(context); return; }
        }
        catch (InvalidOperationException) { await RequestContracts.Problem(context, StatusCodes.Status409Conflict, "idempotency_key_reused", "Idempotency key cannot be reused").ExecuteAsync(context); return; }

        var originalBody = context.Response.Body;
        await using var response = new MemoryStream();
        context.Response.Body = response;
        try
        {
            await next(context);
            response.Position = 0;
            var text = await new StreamReader(response, Encoding.UTF8, leaveOpen: true).ReadToEndAsync(context.RequestAborted);
            var reissue = context.Items.ContainsKey("platform.idempotency.reissue");
            await store.CompleteAsync(ownerId, key, context.Response.StatusCode, reissue ? "{}" : text, reissue, context.RequestAborted);
        }
        catch
        {
            await store.ReleaseAsync(ownerId, key, context.RequestAborted);
            throw;
        }
        finally
        {
            response.Position = 0;
            await response.CopyToAsync(originalBody, context.RequestAborted);
            context.Response.Body = originalBody;
        }
    }

    private static async Task<string> FingerprintAsync(HttpContext context)
    {
        context.Request.EnableBuffering();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{context.Request.Method}\n{context.Request.Path}\n{context.Request.QueryString}\n{context.Request.Headers[RequestContracts.LifecycleVersionHeader]}\n"));
        var buffer = new byte[81920];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0) hash.AppendData(buffer, 0, read);
        context.Request.Body.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
