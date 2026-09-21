# Research: Windows Cloud Files Synchronization

**Feature**: [spec.md](spec.md) | **Date**: 2026-09-03 | **Status**: Complete for planning

1. **Cloud Files API** — Windows-supported placeholder, hydration, pin, and Explorer-status integration; avoids kernel driver risk and full mirror storage cost.
2. **SQLite journal** — durable, replayable device-local state separates transient callbacks from authoritative server updates.
3. **Stable-write upload gate** — only closed, unchanged XMP bytes are hashed/uploaded; remote CAS creates a conflict instead of overwriting.
4. **Opaque identity plus path mapping** — paths remain display/local representation; identity/hashes bind remote authority.
5. **Trusted E2E host** — resettable Windows 11 host validates real callbacks/darktable but stays unavailable to untrusted PR code.

See the inherited [Cloud Files research](../003-darktable-version-control/research.md#decision-6-implement-windows-placeholders-with-the-cloud-files-api).
