using Npgsql;
using Platform.Contracts.Exports;
using static Platform.Storage.IngestDb;
namespace Platform.Api.Features.Exports;
public sealed class ExportBasketService(NpgsqlDataSource source, ExportSelectionService selections, IConfiguration? configuration = null)
{
    public async Task<IReadOnlyList<ExportBasket>> ListAsync(string owner, string library, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        if (await ScalarAsync(db, "SELECT 1 FROM export_baskets WHERE owner_id=$1 AND library_id=$2 LIMIT 1", ct, owner, library) is null)
            await ExecuteAsync(db, "INSERT INTO export_baskets(id,owner_id,library_id,name,is_default) VALUES($1,$2,$3,'Export basket',true)", ct, Guid.NewGuid(), owner, library);
        var ids = new List<Guid>();
        await using (var cmd = Command(db, "SELECT id FROM export_baskets WHERE owner_id=$1 AND library_id=$2 ORDER BY is_default DESC,created_at,id", owner, library))
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) { while (await reader.ReadAsync(ct)) ids.Add(reader.GetGuid(0)); }
        var result = new List<ExportBasket>(); foreach (var id in ids) result.Add(await GetAsync(db, owner, library, id, ct));
        await tx.CommitAsync(ct); return result;
    }
    public async Task<ExportBasket> CreateAsync(string owner, string library, string name, CancellationToken ct)
    {
        ValidateName(name); await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        var count = Convert.ToInt64(await ScalarAsync(db, "SELECT count(*) FROM export_baskets WHERE owner_id=$1 AND library_id=$2", ct, owner, library)); if (count >= ExportConfiguration.Limits(configuration).Baskets) throw new ExportProblem("quota_exceeded");
        var id = Guid.NewGuid(); await ExecuteAsync(db, "INSERT INTO export_baskets(id,owner_id,library_id,name,is_default) VALUES($1,$2,$3,$4,$5)", ct, id, owner, library, name.Trim(), count == 0);
        await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.basket_created", id, "accepted", ct);
        var result = await GetAsync(db, owner, library, id, ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<ExportBasket> GetAsync(string owner, string library, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct); return await GetAsync(db, owner, library, id, ct);
    }
    public async Task<ExportBasket?> MutateAsync(string owner, string library, Guid id, long revision, string action, string? name, bool makeDefault, ExportSelection? selection, Guid? item, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct); await using var tx = await db.BeginTransactionAsync(ct); await ExportDatabase.LockAsync(db, owner, library, ct);
        var current = await GetAsync(db, owner, library, id, ct); if (current.Revision != revision) throw new ExportProblem("stale_basket");
        switch (action)
        {
            case "rename":
                ValidateName(name ?? ""); if (makeDefault) await ExecuteAsync(db, "UPDATE export_baskets SET is_default=false WHERE owner_id=$1 AND library_id=$2 AND is_default", ct, owner, library);
                await ExecuteAsync(db, "UPDATE export_baskets SET name=$2,is_default=$3 WHERE id=$1", ct, id, name!.Trim(), makeDefault || current.IsDefault); break;
            case "add":
                if (current.Items.Count >= ExportConfiguration.Limits(configuration).Items) throw new ExportProblem("quota_exceeded");
                var pin = await selections.ResolveAsync(db, owner, library, selection ?? throw new ExportProblem("invalid_request", 400), ct);
                await ExecuteAsync(db, "INSERT INTO export_basket_items(id,basket_id,pin) VALUES($1,$2,$3::jsonb)", ct, pin.ItemId, id, ExportDatabase.Encode(pin)); break;
            case "remove": if (await ExecuteAsync(db, "DELETE FROM export_basket_items WHERE basket_id=$1 AND id=$2", ct, id, item) == 0) throw new ExportProblem("export_not_found", 404); break;
            case "clear": await ExecuteAsync(db, "DELETE FROM export_basket_items WHERE basket_id=$1", ct, id); break;
            case "delete":
                await ExecuteAsync(db, "DELETE FROM export_baskets WHERE id=$1", ct, id);
                if (current.IsDefault) await ExecuteAsync(db, "UPDATE export_baskets SET is_default=true WHERE id=(SELECT id FROM export_baskets WHERE owner_id=$1 AND library_id=$2 ORDER BY created_at,id LIMIT 1)", ct, owner, library);
                break;
            default: throw new ExportProblem("invalid_request", 400);
        }
        if (action != "delete") await ExecuteAsync(db, "UPDATE export_baskets SET revision=revision+1,updated_at=now() WHERE id=$1", ct, id);
        await AuditAsync(db, owner, ExportDatabase.OperationId, "exports.basket_" + action, id, "accepted", ct);
        var result = action == "delete" ? null : await GetAsync(db, owner, library, id, ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<ExportBasket> GetAsync(NpgsqlConnection db, string owner, string library, Guid id, CancellationToken ct)
    {
        string name; bool isDefault; long revision;
        await using (var cmd = Command(db, "SELECT name,is_default,revision FROM export_baskets WHERE id=$1 AND owner_id=$2 AND library_id=$3", id, owner, library))
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) { if (!await reader.ReadAsync(ct)) throw new ExportProblem("export_not_found", 404); name = reader.GetString(0); isDefault = reader.GetBoolean(1); revision = reader.GetInt64(2); }
        var pins = new List<ExportPin>();
        await using (var cmd = Command(db, "SELECT pin::text FROM export_basket_items WHERE basket_id=$1 ORDER BY id", id))
        await using (var reader = await cmd.ExecuteReaderAsync(ct)) { while (await reader.ReadAsync(ct)) pins.Add(ExportDatabase.Decode<ExportPin>(reader.GetString(0))); }
        var availability = await ExportSelectionService.AvailabilityBatchAsync(db, owner, library, pins, false, ct);
        var items = new List<ExportItemView>(); foreach (var pin in pins) { var reason = availability[pin.ItemId]; items.Add(new(pin, reason is null ? "ready" : "unavailable", "not_requested", ExportNames.For(pin, ExportPreset.Get("jpeg-2048-private-v1")), reason)); }
        return new(id, name, isDefault, revision, items);
    }
    private static void ValidateName(string name) { if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl)) throw new ExportProblem("invalid_name", 400); }
}
