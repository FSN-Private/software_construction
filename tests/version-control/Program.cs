using Platform.Contracts.VersionControl;
using Platform.Api.Features.VersionControl.Integrity;

var manifest = new VersionControlManifest(
    "library-1",
    [],
    "owner-1",
    DateTimeOffset.Parse("2026-09-10T00:00:00Z"),
    DateTimeOffset.Parse("2026-09-10T00:00:01Z"),
    "Initial edit",
    [new(Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222"), "added", null, new string('a', 64))]);
var bytes = VersionControlManifestCodec.Write(manifest);
var decoded = VersionControlManifestCodec.Read(bytes);
Assert(decoded.LibraryId == manifest.LibraryId && decoded.Parents.SequenceEqual(manifest.Parents) && decoded.Entries.SequenceEqual(manifest.Entries), "Canonical manifest must round-trip without changing its value.");
Assert(VersionControlManifestCodec.Write(decoded).SequenceEqual(bytes), "Manifest bytes must be stable after a compatible read.");
Assert(VersionControlManifestCodec.Digest(manifest) == VersionControlManifestCodec.Digest(bytes), "Commit identity must digest the exact canonical bytes.");
var fixtureBytes = File.ReadAllBytes("packages/contracts/version-control/fixtures/manifest-v1.json");
var fixtureDigest = File.ReadAllText("packages/contracts/version-control/fixtures/manifest-v1.sha256").Trim();
Assert(fixtureBytes[^1] == (byte)'\n' && VersionControlManifestCodec.Digest(fixtureBytes.AsSpan(0, fixtureBytes.Length - 1)) == fixtureDigest, "The v1 canonical manifest fixture digest must remain stable.");

var first = Stored(manifest);
var childManifest = manifest with { Parents = [first.Id], Message = "Second edit", ReceivedAt = manifest.ReceivedAt.AddSeconds(1) };
var second = Stored(childManifest);
Assert(GraphIntegrity.Verify("library-1", [first, second]).Healthy, "A valid manifest DAG must pass integrity verification.");
Assert(GraphIntegrity.Verify("library-1", [second]).Codes.Contains("missing_parent"), "Missing parents must be detected.");
Assert(GraphIntegrity.Verify("library-1", [first with { Id = new string('f', 64) }]).Codes.Contains("manifest_digest_mismatch"), "Changed commit identities must be detected.");

Console.WriteLine("Version-control contract checks passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static StoredCommit Stored(VersionControlManifest value)
{
    var canonical = VersionControlManifestCodec.Write(value);
    return new(VersionControlManifestCodec.Digest(canonical), value.LibraryId, canonical, value.Parents);
}
