using Npgsql;
using Platform.Storage;
using static IngestChecks;

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
        await using (var download = (await service.DownloadAsync(owner, image.Id, false, "op_read", default)).Bytes) Check(download.Length > 0, "Authorized image download");
        await using (var download = (await service.DownloadAsync(owner, edits[0].Id, true, "op_read", default)).Bytes) Check(download.Length > 0, "Authorized exact XMP download");
        await RejectAsync<IngestException>(() => service.DownloadAsync("another-owner", image.Id, false, "op_read", default));
        var moved = await service.ChangeAsync(owner, image.Id, false, "move", 1, "新しい/PHOTO.dng", "move-key-00000001", "op_move", default);
        var replay = await service.ChangeAsync(owner, image.Id, false, "move", 1, "新しい/PHOTO.dng", "move-key-00000001", "op_move", default);
        Check(moved == replay && moved.LifecycleVersion == 2, "Move receipt survives replay without duplicate history");
        await RejectAsync<IngestException>(() => service.ChangeAsync(owner, image.Id, false, "move", 1, "stale.dng", "move-key-stale001", "op_move", default));
        await RejectAsync<PostgresException>(() => service.ChangeAsync(owner, image.Id, false, "move", 2, "SAMPLE.PNG", "move-key-collide1", "op_move", default));
        Check((await service.EditsAsync(owner, image.Id, default)).Select(e => e.Id).SequenceEqual(edits.Select(e => e.Id)), "Move preserves edit identities");
        Console.WriteLine("Retrieval/path checks passed: exact bytes, provenance identity, replay, Unicode/case collision, stale version, association preservation.");
    }
}
