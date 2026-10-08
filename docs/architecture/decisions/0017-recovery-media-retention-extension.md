# ADR-0017: Extend exact media-version retention on reuse

**Status**: Accepted for the explicitly approved local design/IAM implementation and tests on 2026-10-08; deployment and live mutations remain unauthorized
**Date**: 2026-10-08
**Decision owner**: Product owner
**Related feature**: [011 backup and disaster recovery](../../features/011-backup-disaster-recovery/spec.md), T009/T018/T035/T036

## Context

The provider's default 90-day lock starts when a version is created. The existing adapter requires protection until each new writer admission plus 90 days. Reusing unchanged content at a later admission therefore fails the existing check; simply removing that check was rejected by automatic approval review. Recopying the entire library at every capture would defeat content-addressed reuse and workload/cost objectives.

## Proposed decision

Preserve the existing check and extend only a reused exact protected media version when necessary. After independent receipt authentication, fenced admission, exact-version metadata and current decrypt access checks, read its retention. If it does not cover the new admission plus 90 days, require governance mode and extend to recovery UTC now plus 91 days. The one-day margin accommodates retention-day rounding without unbounded duration. Never shorten or bypass a lock. Re-read provider retention before settling/signing the new receipt; an unknown response cannot establish protection. Full-byte verification timestamps retain their existing independent cadence.

The only new privilege is the statement in `packages/infrastructure/policy-proposals/recovery-retention-extension.v1.json`: copy-role PutObjectRetention on the exact recovery bucket's `payload/sha256/*`, governance mode, bounded 90–92 remaining days. It adds no bypass, legal-hold, snapshot/evidence, primary, delete, signer, secret or deployment privilege. [AWS requires a separate permission to bypass governance retention](https://docs.aws.amazon.com/AmazonS3/latest/API/API_PutObjectRetention.html) and [supports bounds on remaining retention days](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html).

Logical lifecycle/history/hold eligibility remains independent and fail-closed. Purge must wait for the observed provider lock too; this design does not authorize release of an indefinite hold or removal of a protected final copy. New admissions can delay physical deletion by up to their actual provider protection, so the runbook must report that pending state without claiming purge completion.

## Alternatives and consequences

- Remove the fresh-admission 90-day check: rejected; this weakens protection.
- Write another payload version for every capture: rejected; repeated library transfers and version growth defeat measured cost/RPO objectives.
- Extend the existing exact version: proposed; adds bounded retention requests while preserving bytes, version identity and protection. Native cross-account IAM/provider behavior and request cost still require qualification.

Tests must cover unchanged receipt reuse after the initial retention date, sufficient-retention no-op, wrong/missing mode, denied extension, accepted-but-lost response, insufficient observed extension, exact version binding, no bypass and unchanged full-byte cadence. Rollback disables reuse/schedules and retains existing provider locks; it never reverses an extension. No deployment or live AWS mutation is authorized.
