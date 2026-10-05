using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Platform.Recovery.Worker;

public sealed class RecoveryWorkerConfiguration
{
    public RecoveryWorkerConfiguration(string mode)
    {
        Mode = mode;
        EnvironmentName = Required("Environment");
        WorkerId = Optional("WorkerId") ?? mode;
        WorkerBuildDigest = RequiredHash("RecoveryWorkerBuildDigest");
    }

    public string Mode { get; }
    public string EnvironmentName { get; }
    public string WorkerId { get; }
    public string WorkerBuildDigest { get; }

    public string Required(string name) => Optional(name) ?? throw new RecoveryWorkerException($"config_{name.ToLowerInvariant()}_required");
    public string? Optional(string name) => Environment.GetEnvironmentVariable($"Recovery__{name}");
    public string RequiredHash(string name)
    {
        var value = Required(name);
        if (value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new RecoveryWorkerException($"config_{name.ToLowerInvariant()}_invalid");
        return value.ToLowerInvariant();
    }

    public string RequiredRegion(string name)
    {
        var value = Required(name);
        if (value.Split('-').Length < 3 || value.Any(character => !(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')))
            throw new RecoveryWorkerException($"config_{name.ToLowerInvariant()}_invalid");
        return value;
    }

    public string RequiredAccountId(string name)
    {
        var value = Required(name);
        if (value.Length != 12 || value.Any(character => !char.IsAsciiDigit(character)))
            throw new RecoveryWorkerException($"config_{name.ToLowerInvariant()}_invalid");
        return value;
    }

    public static RecoveryWorkerConfiguration Load(string mode) => new(mode);

    public static async Task<string> ComputeMigrationDigestAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory)) throw new RecoveryWorkerException("migration_artifacts_missing");
        var files = Directory.EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal).ToArray();
        if (files.Length == 0) throw new RecoveryWorkerException("migration_artifacts_missing");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file) + "\0"));
            await using var stream = File.OpenRead(file);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public sealed class RecoveryWorkerException(string safeCode, Exception? innerException = null)
    : Exception(safeCode, innerException)
{
    public string SafeCode { get; } = safeCode;
}

public static class RecoveryWorkerMetrics
{
    public static void Emit(string name, double value, RecoveryWorkerConfiguration configuration)
    {
        var unit = name == "VerifiedPointAgeSeconds" ? "Seconds" : "Count";
        var metric = new Dictionary<string, object?>
        {
            ["_aws"] = new
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = new[]
                {
                    new
                    {
                        Namespace = "Platform/Recovery",
                        Dimensions = new[] { new[] { "Environment", "WorkerId" } },
                        Metrics = new[] { new { Name = name, Unit = unit } }
                    }
                }
            },
            ["Environment"] = configuration.EnvironmentName,
            ["WorkerId"] = configuration.WorkerId
        };
        metric[name] = value;
        Console.WriteLine(JsonSerializer.Serialize(metric));
    }

    public static void EmitFailure(string mode, string safeCode)
    {
        var environment = Environment.GetEnvironmentVariable("Recovery__Environment") ?? "unknown";
        var workerId = Environment.GetEnvironmentVariable("Recovery__WorkerId") ?? mode;
        var metricName = mode switch
        {
            "capture" => "CaptureFailures",
            "copy" => "CopyFailures",
            "verify" or "drill" => "VerificationFailures",
            _ => "WorkerFailures"
        };
        var configuration = new RecoveryWorkerConfigurationForMetrics(environment, workerId);
        var metric = new Dictionary<string, object?>
        {
            ["_aws"] = new
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CloudWatchMetrics = new[]
                {
                    new
                    {
                        Namespace = "Platform/Recovery",
                        Dimensions = new[] { new[] { "Environment", "WorkerId" } },
                        Metrics = new[] { new { Name = metricName, Unit = "Count" } }
                    }
                }
            },
            ["Environment"] = configuration.Environment,
            ["WorkerId"] = configuration.WorkerId,
            ["SafeCode"] = safeCode,
            [metricName] = 1
        };
        Console.WriteLine(JsonSerializer.Serialize(metric));
    }

    private sealed record RecoveryWorkerConfigurationForMetrics(string Environment, string WorkerId);
}
