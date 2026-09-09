using Npgsql;
using Platform.Storage;
using static Platform.Storage.IngestDb;
using static IngestChecks;

internal static class LifecycleChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        const string owner = "test-owner";
        var utc = DateTime.UtcNow;
        var clock = new TestClock(new DateTimeOffset(new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc)));
        var service = new AssetService(db, storage, clock);
        var assets = await service.ListAsync(owner, false, null, default);
        var image = assets.Single(a => a.Path == "新しい/PHOTO.dng");
        var edits = await service.EditsAsync(owner, image.Id, default);
        var deleted = await service.ChangeAsync(owner, image.Id, false, "delete", 2, null, "delete-key-000001", "op_delete", default);
        Check(deleted.DeletedUntil == clock.GetUtcNow().AddDays(90).UtcDateTime, "Deletion has exact 90-day deadline");
        Check(!(await service.ListAsync(owner, false, null, default)).Any(a => a.Id == image.Id), "Deleted asset excluded from active listing");
        await RejectAsync<IngestException>(() => service.DownloadAsync(owner, edits[0].Id, true, "op_read", default));
        clock.Now = clock.Now.AddDays(90);
        var restored = await service.ChangeAsync(owner, image.Id, false, "restore", 3, null, "restore-key-00001", "op_restore", default);
        Check(restored.Id == image.Id && restored.State == "restored", "Restore succeeds at the exact day-90 deadline");
        await using (var download = (await service.DownloadAsync(owner, edits[0].Id, true, "op_read", default)).Bytes) Check(download.Length > 0, "Restore recovers sidecar access");
        await using (var connection = await db.OpenConnectionAsync())
            await ExecuteAsync(connection, "INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($1,$2,'protected-history','test-retained-reference')", default, Guid.NewGuid(), image.Id);
        await RejectAsync<IngestException>(() => service.ChangeAsync(owner, image.Id, false, "delete", 4, null, "hold-delete-00001", "op_delete", default));
        var png = assets.Single(a => a.Path == "sample.png");
        await service.ChangeAsync(owner, png.Id, false, "delete", 1, null, "delete-key-png001", "op_delete", default);
        var copy = assets.Single(a => a.Path == "copy.png");
        await service.ChangeAsync(owner, copy.Id, false, "move", 1, "sample.png", "move-copy-png001", "op_move", default);
        await RejectAsync<PostgresException>(() => service.ChangeAsync(owner, png.Id, false, "restore", 2, null, "restore-png-00001", "op_restore", default));
        await service.ChangeAsync(owner, png.Id, false, "restore", 2, "alternate.png", "restore-png-00002", "op_restore", default);
        await service.ChangeAsync(owner, copy.Id, false, "delete", 2, null, "delete-copy-png1", "op_delete", default);
        clock.Now = clock.Now.AddDays(90).AddTicks(10);
        await RejectAsync<IngestException>(() => service.ChangeAsync(owner, copy.Id, false, "restore", 3, null, "restore-copy-png1", "op_restore", default));
        Check((await service.ListAsync(owner, false, null, default)).Any(a => a.Id == png.Id), "Deleting shared content preserves another asset");
        Console.WriteLine("Lifecycle checks passed: identity, replay, Unicode/case collision, day-90, expiry, hold, restore conflict, shared bytes.");
    }
}

internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
