using Platform.Contracts.Exports;
using Platform.Contracts.Rendering;
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
if (Environment.GetEnvironmentVariable("EXPORT_INTEGRATION") == "1") await ExportIntegration.RunAsync();
static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Reject(Action action) { try { action(); } catch (ExportProblem) { return; } throw new InvalidOperationException("Expected safe rejection"); }
