using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Platform.Api.Features.Gallery;
using Platform.Api.Features.Gallery.Browse;
using Platform.Api.Features.Rendering;
using Platform.Api.Features.VersionControl.Query;
using Platform.Contracts.Rendering;

internal static class GalleryLiveBrowserChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, IAmazonS3 s3, string bucket, string owner, string library, string sourceSha)
    {
        var image = await File.ReadAllBytesAsync("tests/fixtures/ingest/sample.jpg");
        var imageSha = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        var assets = new List<Guid>();
        await using (var command = db.CreateCommand("SELECT asset_id FROM gallery_projection WHERE owner_id=$1 AND library_id=$2 AND rating=4 ORDER BY captured_at DESC LIMIT 40"))
        {
            command.Parameters.AddWithValue(owner);
            command.Parameters.AddWithValue(library);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) assets.Add(reader.GetGuid(0));
        }
        if (assets.Count != 40) throw new InvalidOperationException("Live gallery reference page does not contain 40 cards.");
        foreach (var asset in assets)
        {
            var key = $"gallery-capacity/{asset:D}.jpg";
            using var stream = new MemoryStream(image);
            var stored = await s3.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, InputStream = stream, ContentType = "image/jpeg" });
            var edit = Guid.NewGuid();
            var request = Guid.NewGuid();
            var rendition = Guid.NewGuid();
            await ExecuteAsync(db, "INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance) VALUES($1,$2,0,$3,'gallery-capacity')", edit, asset, sourceSha);
            await ExecuteAsync(db, "UPDATE gallery_projection SET xmp_sha256=$1,projection_state='complete' WHERE asset_id=$2 AND owner_id=$3 AND library_id=$4", sourceSha, asset, owner, library);
            await ExecuteAsync(db, """
                INSERT INTO render_requests(id,owner_id,library_id,request_key,asset_id,edit_id,selected_version,rendition,profile,renderer_release,renderer_artifact_digest,matrix_version,source_sha256,xmp_sha256,state)
                SELECT $1,$2,$3,$4,$5,$6,'fixture','thumbnail-512',profile,renderer_release,renderer_artifact_digest,matrix_version,$7,$7,'completed'
                FROM rendering_compatibility WHERE matrix_version='rendering-v1'
                """, request, owner, library, Convert.ToHexString(SHA256.HashData(request.ToByteArray())).ToLowerInvariant(), asset, edit, sourceSha);
            await ExecuteAsync(db, "INSERT INTO renditions(id,request_id,object_key,object_version,output_sha256,byte_length,media_type,completed_at) VALUES($1,$2,$3,$4,$5,$6,'image/jpeg',now())",
                rendition, request, key, stored.VersionId, imageSha, (long)image.Length);
            await ExecuteAsync(db, """
                INSERT INTO render_provenance(rendition_id,source_sha256,xmp_sha256,selected_version,renderer_release,renderer_artifact_digest,profile,matrix_version,completed_at)
                SELECT $1,$2,$2,'fixture',renderer_release,renderer_artifact_digest,profile,matrix_version,now()
                FROM rendering_compatibility WHERE matrix_version='rendering-v1'
                """, rendition, sourceSha);
        }

        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Configuration["Gallery:LibraryId"] = library;
        builder.Configuration["Rendering:DerivativeBucket"] = bucket;
        builder.Services.AddAuthorization();
        builder.Services.AddDataProtection();
        builder.Services.AddSingleton(db);
        builder.Services.AddSingleton<GalleryBrowseService>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.Authorization == "Bearer fixture-token")
                context.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim("sub", owner), new Claim("cognito:groups", "owner")
                ], "gallery-capacity-fixture"));
            await next();
        });
        app.UseAuthorization();
        GalleryEndpoints.Map(app);
        var rendering = new RenderingService(db, new VersionControlSelectedVersionResolver(new QueryService(db)), builder.Configuration,
            locator: new LocalHttpS3Locator(s3, builder.Configuration));
        app.MapGet("/v1/renditions/{requestId:guid}/derivative", async (Guid requestId, string library) =>
            Results.Ok(await rendering.LocateAsync(owner, library, requestId, default))).RequireAuthorization();
        await app.StartAsync();
        try
        {
            var webDirectory = Path.Combine(Directory.GetCurrentDirectory(), "apps", "web");
            var windows = OperatingSystem.IsWindows();
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(windows ? "cmd.exe" : "npm")
                {
                    WorkingDirectory = webDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            if (windows) process.StartInfo.ArgumentList.Add("/c");
            if (windows) process.StartInfo.ArgumentList.Add("npm");
            process.StartInfo.ArgumentList.Add("run");
            process.StartInfo.ArgumentList.Add("test:browser");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add("--grep");
            process.StartInfo.ArgumentList.Add("live-reference");
            if (Environment.GetEnvironmentVariable("GALLERY_LIVE_PROJECT") is { Length: > 0 } project)
            {
                process.StartInfo.ArgumentList.Add("--project");
                process.StartInfo.ArgumentList.Add(project);
            }
            process.StartInfo.Environment["GALLERY_LIVE_API"] = $"http://127.0.0.1:{port}";
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Console.WriteLine(await output);
            Console.WriteLine(await errors);
            if (process.ExitCode != 0) throw new InvalidOperationException("Live 100,000-asset browser reference check failed.");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task ExecuteAsync(NpgsqlDataSource db, string sql, params object?[] values)
    {
        await using var command = db.CreateCommand(sql);
        foreach (var value in values) command.Parameters.AddWithValue(value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class LocalHttpS3Locator(IAmazonS3 s3, IConfiguration configuration) : IPrivateDerivativeLocator
    {
        private readonly S3PrivateDerivativeLocator inner = new(s3, configuration);

        public string Locate(RenderPublication publication)
        {
            var url = inner.Locate(publication);
            return url.StartsWith("https://127.0.0.1:", StringComparison.Ordinal)
                ? "http://" + url[8..]
                : url;
        }
    }
}
