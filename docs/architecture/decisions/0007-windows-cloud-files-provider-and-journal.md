# ADR-0007: Windows Cloud Files Provider and Journal

**Date**: 2026-09-03

**Status**: Accepted

**Decision owners**: Project maintainer

**Feature**: [007 Windows Cloud Files Synchronization](../../features/007-windows-cloud-files-sync/spec.md)

## Context and drivers

Darktable needs ordinary stable paths, but a multi-terabyte remote library cannot be mirrored to every laptop. Offline XMP work must survive crashes and reconnect without turning network errors, branch changes, or local cleanup into data loss. The boundary must also remain testable without granting untrusted pull requests access to a privileged desktop host.

## Decision

Use the Windows Cloud Files API through a versioned .NET adapter and store replayable device state in a local SQLite journal. Placeholder metadata contains only versioned opaque asset/edit/content identities, expected length, and SHA-256. Hydration downloads and verifies a complete object before reporting success. Stable local XMP bytes enter the journal before an expected-base, idempotent feature-006 working-state request. Tokens use current-user Windows DPAPI storage outside the journal. Cloud Files, packaging, and darktable tests run only on a resettable manually dispatched Windows 11 host; ordinary pull requests use model tests with no credentials.

## Options considered

- A full local mirror is operationally simple but violates the storage and online-only requirements.
- WebDAV does not provide Windows placeholder, recursive pin, hydration callback, or reliable offline-journal semantics.
- A kernel filter driver offers control at a substantially larger signing, security, recovery, and maintenance cost.
- A path-keyed or direct-upload client is smaller but cannot preserve identity across rename or recover exactly once after interruption.
- Doing nothing leaves darktable without the required Windows/offline experience.

## Consequences and controls

The design is Windows-specific and must maintain native structure/callback compatibility. SQLite and DPAPI add two Microsoft package inputs, both locked and audited. Callback work is isolated from network/journal coordination, every mutation carries a durable idempotency identity, and dirty/pending/conflicted content cannot auto-dehydrate. Diagnostics use opaque operation IDs and counters, not content, tokens, or paths. The companion status model supplies textual and keyboard-accessible states instead of color-only meaning.

Journal readers remain backward compatible before writers advance schema. MSIX packages are produced for x64 and ARM64 and must be SHA-256 signed and verified before distribution. Rollback is allowed only while the prior client understands the journal/protocol version; otherwise export local data, unregister the root, and install cleanly. Revocation stops remote work but deliberately retains local bytes until the owner exports or explicitly removes them.

## Risks and follow-up

Cloud Files callbacks race with Explorer, darktable, antivirus, and shutdown, so the fault matrix and trusted-host evidence are release gates. A missing signing identity, test host, or production endpoint blocks distribution, not local implementation. Task tracking, validation, upgrade rehearsal, and operating guidance remain in the feature 007 artifact set. No deployment, certificate use, device registration, or remote mutation is authorized by this ADR alone.
