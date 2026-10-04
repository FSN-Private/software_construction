using Platform.Contracts.Exports;
namespace Platform.Api.Features.Exports;

public static class ExportConfiguration
{
    public static void DownloadCors(Microsoft.AspNetCore.Cors.Infrastructure.CorsPolicyBuilder policy, string? origin)
    {
        if (!string.IsNullOrEmpty(origin)) policy.WithOrigins(origin).WithMethods("GET", "HEAD", "POST").WithHeaders("Authorization", "Range", "If-Range", "Content-Type", "X-Operation-Id").WithExposedHeaders("ETag", "X-Content-SHA256", "Accept-Ranges", "Content-Range", "Content-Length", "Content-Disposition").AllowCredentials();
    }
    public static ExportLimits Limits(IConfiguration? config)
    {
        var limits = new ExportLimits(config?.GetValue<int?>("Exports:Items") ?? 250, config?.GetValue<int?>("Exports:Baskets") ?? 20, config?.GetValue<int?>("Exports:Requests") ?? 10, config?.GetValue<long?>("Exports:PackageBytes") ?? 10L * 1024 * 1024 * 1024, config?.GetValue<int?>("Exports:PreparationSeconds") ?? 7200, config?.GetValue<int?>("Exports:RetentionHours") ?? 24);
        limits.Validate(); return limits;
    }
}
