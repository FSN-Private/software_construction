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

    public static Task<Guid> RecordChangeAsync(
        NpgsqlConnection db,
        NpgsqlTransaction tx,
        string owner,
        string library,
        string eventType,
        string entityType,
        string entityId,
        IReadOnlyCollection<string> contentDigests,
        CancellationToken ct,
        string outcome = "accepted",
        string? code = null)
    {
        var resourceId = Guid.TryParse(entityId, out var parsed) ? parsed : Guid.Empty;
        return Platform.Storage.IngestDb.AuditAndRecordRecoveryChangeAsync(
            db, tx, owner, library, OperationId, eventType, resourceId, outcome,
            entityType, entityId, contentDigests, ct, code);
    }

    public static IReadOnlyList<string> ContentDigests(ExportSnapshot snapshot) =>
        snapshot.Items.SelectMany(pin => new[] { pin.Original.Sha256, pin.Xmp.Sha256, pin.CommitId })
            .Where(Platform.Contracts.Exports.ExportIntegrity.IsSha256)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static async Task<IReadOnlyList<string>> RequestContentDigestsAsync(
        NpgsqlConnection db,
        Guid requestId,
        CancellationToken ct)
    {
        var raw = await ScalarAsync(db, "SELECT snapshot::text FROM export_requests WHERE id=$1", ct, requestId) as string
            ?? throw new ExportProblem("export_not_found", 404);
        return ContentDigests(Decode<ExportSnapshot>(raw));
    }
}
