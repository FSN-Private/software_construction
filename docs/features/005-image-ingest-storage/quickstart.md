# Image Ingest and Storage Validation Quickstart

Use the approved format/XMP corpus. Import a batch containing supported RAW/JPEG/PNG/TIFF files, base and numbered darktable sidecars, duplicate bytes, interrupted transfer, malformed XMP, corrupt bytes, and an ambiguous sidecar. Verify progress starts within two seconds; every item has one deterministic result; only verified, associated items are active; and source, storage, and download SHA-256 values match.

Retry completion and restart the worker between object upload and metadata publication. Verify no acknowledged orphan or duplicate logical asset remains and reconciliation records its repair. Move and case-only rename a 10,000-asset corpus; verify IDs, associations, and path history. Soft-delete then restore at the 90-day boundary, including an intentional destination conflict and a retained-reference hold. Export a manifest, seed missing/altered/extra/mis-associated records, and confirm all are detected.
