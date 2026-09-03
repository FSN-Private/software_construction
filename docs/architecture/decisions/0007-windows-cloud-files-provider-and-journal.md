# ADR-0007: Windows Cloud Files Provider and Journal

**Date**: 2026-09-03  
**Status**: Proposed  
**Feature**: [007 Windows Cloud Files Synchronization](../../features/007-windows-cloud-files-sync/spec.md)

Use Cloud Files API behind a versioned .NET adapter and SQLite replay journal. Placeholders bind opaque remote identities; complete hydration validates checksums; stable local XMP uploads use expected-base/idempotency contracts. This avoids WebDAV/full-mirror/kernel-driver weaknesses and preserves local/remote divergence. The cost is Windows-specific callback and signing/test-host complexity. No distribution or device access is authorized by this ADR.
