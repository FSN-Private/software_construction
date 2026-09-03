# Implementation Plan: Windows Cloud Files Synchronization

**Branch**: `feature/007-windows-cloud-files-sync` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)
**Status**: Proposed — pending approval

## Summary

Deliver a .NET 10 Windows 11 sync provider using the Cloud Files API. One registered sync root per owner/device materializes branch-scoped opaque-identity placeholders, hydrates only verified content on demand, journals durable local operations in SQLite, and uploads stable XMP changes through feature-006 expected-base/idempotent working-state APIs. Filesystem deletion, dehydration, and local edit are distinct operations; no automatic commit or overwrite is permitted.

The Cloud Files adapter/journal boundary is recorded in proposed [ADR-0007](../../architecture/decisions/0007-windows-cloud-files-provider-and-journal.md).

## Technical Context

| Area | Decision |
|---|---|
| Runtime | .NET 10/C# 14 Windows desktop service plus companion setup/status UI; Cloud Files native API isolated behind adapter |
| Local state | SQLite journal protected by OS ACL; tokens in Windows Credential Manager/DPAPI, never in placeholders/journal |
| Remote contract | feature-005 content/identity plus feature-006 branch delta, checkout, working-change, conflict, and expected-base APIs |
| Placeholder | opaque asset/edit/content ID, expected size/hash and state; mutable path is not authority |
| Hydration | range-aware callbacks write private temporary data, validate full object hash before completion, then atomically expose content |
| Upload | watcher/callback records change; stability gate requires close/exclusive-read and unchanged size/hash before idempotent expected-base publish |
| Targets | 1,000 placeholder listing ≤2s p95; hydration starts ≤2s p95; 100k namespace/5 devices without full local mirror |

## Constitution Check

- **PASS**: feature consumes platform/storage/version contracts and does not recreate their authority.
- **PASS**: branch is correct, diverges from `master`, and no history reconciliation occurs.
- **PASS**: native calls are adapter-isolated; durable journal/replay, scoped credentials, exact hydration, conflict preservation, and signed distribution are explicit.
- **GATE**: trusted signing, device registration, Windows 11 host, and external deployment remain unauthorized.

## Design

```text
Cloud Files callbacks ↔ adapter ↔ SQLite journal ↔ sync coordinator ↔ 005/006 APIs
       ↓                         ↓                         ↓
 placeholders/status       replayable operations       working changes/conflicts
```

Register one sync root for a device/library. Its branch cursor produces a namespace whose files hold opaque remote identity and availability/pin state. Hydration verifies identity and hash before Windows is told a file is complete; interruption leaves only incomplete/retryable state. Recursive pin queues every child with durable outcome. Clean unpinned hydrated files may dehydrate; pinned, dirty, pending, or conflicted files never do.

The SQLite journal records monotonic local/remote sequence, branch/cursor, base/current hash, pin/availability, pending upload/move/delete/restore, conflict, and acknowledgement. It has no raw credential/XMP/image content. Provider restart replays idempotently. A stability detector waits for a closed, unchanged XMP byte state before hashing/upload. Feature 006 rejects stale bases and returns divergence; the provider preserves the local bytes and reports a conflict rather than replacing them. Server-applied working changes use the same dirty/conflict guard.

Checkout first asks feature 006 to classify dirty state. It blocks, preserves/stashes, or requires confirmed discard, then reconciles a verified delta against the new per-device branch. Path mapping rejects/escapes case collisions, reserved/invalid names, excessive paths, and Unicode ambiguities deterministically while retaining opaque identity. Offline delete/restore intents are journaled; dehydration never becomes deletion. Revocation stops remote operations but leaves local bytes for owner-directed export/cleanup.

[windows-sync-v1.md](contracts/windows-sync-v1.md) fixes client/server states. The companion UI makes all listed status/error states accessible. Logging/audit uses operation IDs and redaction. Tests run unit/model tests on hosted CI plus callback/darktable/offline/race fault tests on a resettable trusted Windows 11 host; untrusted PRs never receive that host's credentials. Upgrades version journal/protocol readers before writers; rollback is supported only while compatible.

## Alternatives and Post-design Check

WebDAV, full mirror, filesystem filter driver, path-as-identity, direct uploads without journal, and last-writer-wins are rejected for offline, security, or no-loss reasons. Exact installer/signing product and callback paging are implementation decisions constrained by the stated tests.

**PASS.** No material product ambiguity remains; distribution and external service changes await explicit authorization.
