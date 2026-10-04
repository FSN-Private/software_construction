using Platform.Contracts.Exports;
using Platform.Contracts.Rendering;
using Platform.Api.Features.Exports;
var sha = new string('a', 64);
var pin = new ExportPin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('b', 64), new RenderInputObject("originals/a", "v1", sha, 4), new RenderInputObject("xmp/a", "v1", new string('c', 64), 4), "CON. RAW", "historical", "main", false, DateTimeOffset.UnixEpoch);
pin.Validate(); var original = ExportPreset.Get("originals-v1");
Check(ExportNames.For(pin, original).StartsWith("image-CON-"), "Windows names must avoid reserved basenames");
var names = Enumerable.Range(0, 250).Select(_ => ExportNames.For(pin with { ItemId = Guid.NewGuid() }, original)).ToArray();
Check(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 250, "Every repeated selection must have its own output name");
var snapshot = new ExportSnapshot(1, Guid.NewGuid(), original.Id, true, DateTimeOffset.UnixEpoch, [pin]);
new ExportManifest(1, snapshot, [new(pin.ItemId, "completed", null, ExportNames.For(pin, original), 4, sha)]).Validate();
Reject(() => new ExportManifest(1, snapshot, [new(pin.ItemId, "completed", null, ExportNames.For(pin, original), 4, new string('d', 64))]).Validate());
Reject(() => new ExportManifest(2, snapshot, []).Validate()); new ExportLimits().Validate(); Reject(() => new ExportLimits(Items: 251).Validate());
byte[] jpeg = [0xff, 0xd8, 0xff, 0xe1, 0, 8, 71, 80, 83, 0, 1, 2, 0xff, 0xda, 0, 2, 12, 0xff, 0, 15, 0xff, 0xd9];
var stripped = JpegMetadata.Transform(jpeg, false);
Check(!stripped.AsSpan().Contains((byte)71) && stripped.Length == jpeg.Length - 10, "Private transform strips descriptive metadata");
Check(JpegMetadata.Transform(jpeg, true).SequenceEqual(jpeg), "Confirmed retain transform preserves metadata"); Reject(() => JpegMetadata.Transform(jpeg[..^1], false));
var purge = new ExportPurgeEvent(Guid.NewGuid(), "owner", "library", pin.AssetId, pin.EditId, "xmp", pin.Xmp.Sha256, 1, Guid.NewGuid()); purge.Validate();
Check(!purge.Affects(pin, original.Id) && purge.Affects(pin, "jpeg-2048-private-v1"), "XMP purge does not invalidate originals");
Check(!purge.Affects(pin with { AssetId = Guid.NewGuid() }, "jpeg-2048-private-v1"), "Other assets remain independent");
Console.WriteLine("Export contract checks passed.");
Check(ExportRange.Parse("bytes=1-3", "", "\"hash\"", 10) == (1, 3, true), "Explicit range bounds");
Check(ExportRange.Parse("bytes=-3", "", "\"hash\"", 10) == (7, 9, true), "Suffix ranges");
Check(ExportRange.Parse("bytes=8-", "old", "\"hash\"", 10) == (0, 9, false), "If-Range mismatch returns full immutable representation");
Reject(() => ExportRange.Parse("bytes=10-", "", "\"hash\"", 10)); Reject(() => ExportRange.Parse("bytes=0-1,3-4", "", "\"hash\"", 10));
if (Environment.GetEnvironmentVariable("EXPORT_INTEGRATION") == "1") await ExportIntegration.RunAsync();
if (Environment.GetEnvironmentVariable("EXPORT_JPEG_CORPUS") is { } corpus)
{
    var files = Directory.GetFiles(corpus, "*.jpg"); Check(files.Length == 8, "Four exact XMP selections at two dimensions");
    foreach (var file in files)
    {
        var encoded = await File.ReadAllBytesAsync(file);
        Check(JpegMetadata.Transform(encoded, true).SequenceEqual(encoded), "Real encoded retain JPEG remains byte-identical");
        byte[] metadata = [0xff, 0xe1, 0, 8, 71, 80, 83, 0, 1, 2];
        var injected = encoded[..2].Concat(metadata).Concat(encoded[2..]).ToArray();
        var clean = JpegMetadata.Transform(encoded, false);
        Check(JpegMetadata.Transform(injected, false).SequenceEqual(clean), "Real encoded JPEG removes added descriptive metadata without changing image bytes");
        Check(JpegMetadata.Transform(clean, false).SequenceEqual(clean), "Private JPEG transform is idempotent");
    }
    Check(files.Select(f => ExportIntegrity.Hash(File.ReadAllBytes(f))).Distinct().Count() == 8, "Actual historical XMP renders remain distinguishable");
    Console.WriteLine("Eight real encoded JPEG metadata and exact-XMP distinctness checks passed.");
}
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Reject(Action action) { try { action(); } catch (ExportProblem) { return; } throw new InvalidOperationException("Expected safe rejection"); }
