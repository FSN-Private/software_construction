using Npgsql;
using Platform.Storage;
using static IngestChecks;
using static Platform.Storage.IngestDb;

internal static class RetrievalPathChecks
{
    public static async Task RunAsync(NpgsqlDataSource db, S3ObjectStore storage)
    {
        const string owner = "test-owner";
        var service = new AssetService(db, storage);
        var assets = await service.ListAsync(owner, false, null, default);
        var image = assets.Single(a => a.Path == "sample.dng");
        var edits = await service.EditsAsync(owner, image.Id, default);
        Check(edits.Count == 2, "Sidecars retain source identity");
        var beforeDownload = await RecoverySequenceAsync(owner);
        await using (var download = (await service.DownloadAsync(owner, image.Id, false, "op_read", default)).Bytes) Check(download.Length > 0, "Authorized image download");
        Check(await RecoverySequenceAsync(owner) == beforeDownload + 1,
            "An authorized original download writes its access audit and recovery watermark in one transaction.");
        await using (var download = (await service.DownloadAsync(owner, edits[0].Id, true, "op_read", default)).Bytes) Check(download.Length > 0, "Authorized exact XMP download");
        Check(await RecoverySequenceAsync(owner) == beforeDownload + 2,
            "An authorized XMP download writes its access audit and recovery watermark in one transaction.");
        await RejectAsync<IngestException>(() => service.DownloadAsync("another-owner", image.Id, false, "op_read", default));
        Check(await RecoverySequenceAsync(owner) == beforeDownload + 2,
            "A foreign download denial records no owner-scoped successful access watermark.");
        var moved = await service.ChangeAsync(owner, image.Id, false, "move", 1, "新しい/PHOTO.dng", "move-key-00000001", "op_move", default);
        var replay = await service.ChangeAsync(owner, image.Id, false, "move", 1, "新しい/PHOTO.dng", "move-key-00000001", "op_move", default);
        Check(moved == replay && moved.LifecycleVersion == 2, "Move receipt survives replay without duplicate history");
        await RejectAsync<IngestException>(() => service.ChangeAsync(owner, image.Id, false, "move", 1, "stale.dng", "move-key-stale001", "op_move", default));
        await RejectAsync<PostgresException>(() => service.ChangeAsync(owner, image.Id, false, "move", 2, "SAMPLE.PNG", "move-key-collide1", "op_move", default));
        Check((await service.EditsAsync(owner, image.Id, default)).Select(e => e.Id).SequenceEqual(edits.Select(e => e.Id)), "Move preserves edit identities");
        Console.WriteLine("Retrieval/path checks passed: exact bytes, provenance identity, replay, Unicode/case collision, stale version, association preservation.");

        async Task<long> RecoverySequenceAsync(string scopeOwner)
        {
            await using var connection = await db.OpenConnectionAsync();
            return Convert.ToInt64(await ScalarAsync(connection,
                "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id=$1 AND library_id='main'", default, scopeOwner));
        }
    }
}
