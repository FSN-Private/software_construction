# Reconstruction manifest verification

Owner: platform maintainer. Feature 005 exports a canonical version-1 inventory. A valid recovery check requires two independently obtained inputs: the previously retained trusted manifest and a manifest reconstructed from the recovery database. Using the same file for both cannot prove recovery.

1. Copy the trusted and reconstructed JSON manifests into a private, access-audited workspace. Export recovered authoritative object bytes into a separate directory using their `sha256/<digest>` relative keys. Reject symlinks/reparse points and do not place unrelated files there.
2. Run `dotnet run --project apps/ingest-worker/Platform.Ingest.Worker.csproj -- --verify-manifest <trusted.json> <recovered.json> <object-directory>`.
3. A successful result is `{"verified":true,"errors":[]}`. Retain the command version, manifest IDs/digests, recovery snapshot ID, operator, timestamp, and result. Do not retain media/XMP payloads in general logs.
4. Any `inventory_altered` or section-specific error means the canonical database relationships differ. `assets`, `edits`, `paths`, `holds`, `audit`, `provenance`, and `objects` must all match, including association provenance and version-control handoff IDs.
5. `object_missing`, `object_altered`, or `object_extra` means the recovered object inventory differs from authority. Preserve all copies and versions, stop restoration, and follow the checksum-drift procedure in [ingest storage operations](ingest-storage.md). Do not delete extras or replace changed objects until feature 011 recovery policy selects the authoritative source.

Repeat after any corrective recovery. Recovery is verified only when all errors are absent and sampled exact-byte downloads succeed through the owner-authorized API.
