using Npgsql;
using System.Text.Json;
using Platform.Contracts.Exports;
using Platform.Api.Features.VersionControl.Adapters;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;

public static class ExportDatabase
{
    public static readonly AsyncLocal<string?> Operation = new();
    public static string OperationId => Operation.Value ?? "op_" + Guid.NewGuid().ToString("N");
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw new ExportProblem("integrity_error");
    public static async Task LockAsync(NpgsqlConnection db, string owner, string library, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(library) || library.Length > 200) throw new ExportProblem("invalid_request", 400);
        await LockOwnerAsync(db, owner, ct);
        await HistoryRetention.LockAsync(db, owner, library, ct);
    }
}
