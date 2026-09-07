using Platform.Contracts;

namespace Platform.Api.Contracts;

public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method)
                ? RequestContracts.HasIdempotencyKey(context)
                ? next(context)
                : RequestContracts.Problem(context, StatusCodes.Status400BadRequest, "idempotency_key_required", "Idempotency key required").ExecuteAsync(context)
            : next(context);
}
