# Implementation Plan: Backup and Disaster Recovery

**Feature branch**: feature/011-backup-disaster-recovery-fresh
**Date**: 2026-10-05
**Spec**: [spec.md](spec.md)
**Status**: Approved for local implementation on 2026-10-05; external qualification remains gated.

**Branch exception**: The owner requested a fresh branch because the prior 011 branch was stale. The canonical artifact directory remains unchanged. This branch starts from origin/master; local master is 43 commits behind it. No history was reconciled.

## Summary

Use a separately administered recovery account in ap-southeast-2 for the current primary region ap-southeast-1. Capture a transactionally consistent PostgreSQL logical snapshot every 15 minutes. A recovery-side worker pulls the dump and its exact referenced S3 object versions, checks its purge journal before each write, and advances the healthy recovery watermark only after independent verification and signing. Rebuild the app from preserved configuration and exact image digests into an isolated recovery environment.

This is a proposed design, not a claim that the RPO, RTO, cost envelope, account, owner identity path, or AWS rehearsal is approved or achieved.

## Technical context and gates

| Area | Current facts and proposed use |
|---|---|
| Runtime | .NET 10 API/workers, Npgsql, AWS S3/SQS SDKs, CDK, Node release tooling. Use a separate apps/recovery-worker with capture and recovery modes, existing .NET storage conventions, and pinned PostgreSQL 18 client tools. |
| Authorities | Versioned KMS-encrypted ingest S3 owns content-addressed originals/XMP; PostgreSQL owns exact object-version references, asset/edit lifecycle, metadata, immutable graph/manifests, refs/reflogs, working state, and audit. Previews/exports and projections are disposable. |
| Current resilience | Foundation stack defaults to ap-southeast-1, has one single-AZ RDS PostgreSQL 18.4 instance with seven-day automated backup retention, and no recovery account, copy worker, restore path, or drill. The BackupAgeHours 26-hour alarm is a placeholder. |
| Target | Separately administered recovery account and ap-southeast-2. Account IDs, aliases, role ARNs, owner mailbox, MFA custody, and alert recipients are deployment inputs, not committed constants. |
| Scale | Existing capacity work covers 100,000 images and 1,000,000 content/version records. Total protected bytes, size distribution, growth, and transfer throughput are unknown; measure these before claiming 24-hour RTO. |
| Work source | The numbered [tasks.md](tasks.md) is the sole execution plan and maps requirements/acceptance criteria. T006 owner approval precedes all code. |

The owner approved the specification, design, documented assumptions and local implementation on 2026-10-05; ADR-0003 is Accepted. No production deployment, account creation, migration, restore, purge, cold transition, or real drill is authorized. Local simulations cannot satisfy release criteria.

## Constitution check

| Principle | Planning assessment |
|---|---|
| I — Approved specification | PASS for local implementation: T006 owner approval was received on 2026-10-05; all requirements remain binding. |
| II — Complete/testable delivery | Each of the 49 FR/AC/NFR/SC IDs maps to numbered implementation and verification work; local fixtures and actual AWS evidence are explicitly separated. |
| III — Quality/supply chain | Reuse installed .NET/Npgsql/AWS SDKs/CDK and existing pinned PostgreSQL tools; review any new dependency, action or container identity before execution. Privacy, access, integrity, performance, accessibility and operational tests are planned. |
| IV — Branch/milestones | Clean starting tree on the explicitly owner-authorized fresh branch from `8b2a121`; canonical directory preserved as a branch-name exception. Commit M0–M5 separately after their gates pass. |
| V — Independent review | T027 requires a genuinely different model, justified remediation and repeated review agreement; a passing review does not authorize pushing or merging. |

All owner-facing text uses the existing localization conventions and accessible text/keyboard controls (WCAG 2.2 AA verification). Status queries must meet p95 <=5 seconds at the existing 100,000-image/1,000,000-record envelope. Access, logs and responses must produce zero unauthorized successes or credential/media/private-path disclosures in the fault corpus. Audit/holds/purge failures remain pending and fail closed. Classification/runbook review is recorded after every quarterly drill and material recovery architecture change. Production cost limits and the measured byte/change-volume envelope are named prerequisites rather than guessed defaults.

## Proposed architecture

### Protected classes

- Copy exact original and XMP S3 object versions identified by the canonical PostgreSQL snapshot. Record source key/version, SHA-256, byte length, category, destination version, and verification receipt.
- Capture the full PostgreSQL schema/data view: lifecycle, paths, identity, metadata, version graph/commit manifests, refs/reflogs, working state, holds, audit, and recovery tables. Existing reconstruction manifests alone are incomplete for this scope.
- Preserve exact CDK templates, non-secret environment descriptor, migration version, release manifest/provenance, and deployed/rollback-eligible API, worker, and renderer image digests. Seed current ECR images before healthy status.
- Rebuild the recovery Cognito pool from IaC. Do not copy primary credentials. Recovery service secrets are reissued. Because the current owner ID is Cognito sub, a new pool creates a different subject. Recovery therefore installs a server-side mapping from the verified (issuer, audience, new subject) tuple to the preserved logical owner ID. The mapping is created only after independent recovery-account MFA approval, stored as signed recovery configuration, and applied only in the recovered deployment; callers cannot supply owner IDs and immutable graph/database ownership is not rewritten. Previews, exports, queues, and gallery projections are recreated.
- Keep recovery evidence (signed checkpoints, audit/purge records, and drill attestations) in recovery-owned versioned storage.

### Capture and controlled copy

1. Every 15 minutes, a primary VPC capture task opens a read-only repeatable-read PostgreSQL transaction, exports its MVCC snapshot, and records its UTC cutoff, WAL LSN, migration version, and greatest committed acknowledged-change sequence visible in that snapshot. Every accepted library mutation must append to this ledger in the same transaction; an idle library is not stale solely because no mutation occurred.
2. Under that same exported snapshot, enumerate protected data-class totals and every exact content dependency. Pass the exported snapshot to pinned PostgreSQL 18 pg_dump in custom format. Hash the completed dump and run pg_restore listing/schema validation before publishing it and its canonical closure manifest to a private, versioned primary capture prefix.
3. A recovery-account copy/verification worker polls the capture prefix every 15 minutes. During normal backup operation only, it assumes a scoped primary read role for the dump, manifest, and exact listed source object versions. It writes to recovery storage with a recovery-account role. The primary runtime cannot read/list/delete recovery data and receives no recovery credentials. Restore mode never assumes or reads a primary role.
4. Before each write and before sealing a point, the recovery worker checks its independent purge journal and durable write intents. New object versions are streamed and fully hashed in recovery. Reuse a receipt only for the same immutable key/version/digest/length mapped to the same protected destination version, with fresh existence, access, and retention-policy checks. Periodic full verification and every full drill rehash bytes. Fully seed and verify all existing objects before declaring protection healthy.
5. An independent recovery-account verifier validates the dump, signed inventory, exact-version closure, release/configuration digests, owner/access policy, and copy state. It signs the recovery-point checkpoint with a recovery-owned KMS key. Only a complete, verified, signed point advances the watermark.

The capture interval is not the RPO. Measure the conservative loss window as declared-loss time minus the cutoff of the newest fully verified recovery point, and prove every acknowledged mutation through that cutoff appears in its transactional watermark. Require capture starts at most 15 minutes apart, warn when verified-point age reaches 45 minutes, alert within 15 minutes of that overdue state, and demonstrate cutoff age at declared loss of at most 3,600 seconds in a real drill. No AWS service schedule guarantees that end-to-end result; a failed measurement blocks release.

### Health, isolated restore, audit, and purge

- Store recovery points, observations, receipts, restore sessions, validation revisions, holds, purge journals, and audit-chain records in packages/recovery and additive migration 022. Add private/no-store owner status and operation endpoints under apps/api/Features/Recovery plus an accessible localized text health view under apps/web/src.
- Return safe status codes, UTC timestamps, class counts, freshness, and exception states. Do not expose filenames, object keys, email, photo metadata, or credentials.
- Restore only through the recovery-account worker into an isolated database and object namespace. Validate hashes, exact S3 versions, XMP/edit identity, metadata, graph integrity, audit continuity, release compatibility, access policy, and owner authorization. Verify the signed server-side mapping for the new Cognito issuer/audience/subject to the original logical owner ID; prove that this owner can access the preserved library and a foreign subject cannot. Promotion requires fresh recovery-account MFA approval bound to the validated point, mapping, and target digest; validation never writes to the live primary target.
- Anchor audit records with a recovery-account-signed immutable checkpoint and a pinned offline fingerprint; fail closed when absent or invalid.
- Mirror only signed safe health summaries back to a dedicated primary SQS result queue, using a narrow recovery-side send role and primary-side receive role. The API verifies the recovery signer and stores observations in its local recovery tables for bounded status queries. Primary roles gain no access to protected recovery data. During regional loss, the independent runner reads the recovery catalog directly instead of depending on this mirror.
- Proposed purge scope is eligible media payload bytes (originals, XMP, and embedded payload copies). A recovery-account MFA identity approves the exact versions and plan digest; primary API claims cannot authorize recovery deletion. Recheck 90-day holds, shared references, feature 012 invalidation/cleanup acknowledgments, all source/destination versions, and in-flight write intents under one durable fence. Publish a recovery-owned tombstone before deletion and prevent older database dumps from restoring those bytes. Retain non-content graph/history/audit metadata, hashes, and paths. Broader private-metadata erasure would require a separate graph-redaction design and is an explicit owner decision.
- Retention and purge share durable intents, recheck immediately before deletion, and cannot remove the final verified copy of any protected class.

### Cold policy and cost gate

Keep recovery payload versions in S3 Standard for their first 90 days, then transition only the recovery-account payload to S3 Glacier Instant Retrieval. Individual objects remain online with millisecond access, but retrieval has per-GB/request fees, a 128 KB minimum billable size, and a 90-day minimum duration; bulk transfer speed still affects RTO. Keep database dumps, manifests/catalog, config/release artifacts, ECR images, access inputs, and signed audit/purge/checkpoint evidence warm. Do not expire the final verified copy. AWS Backup cold tier is not used for this design.

Build the estimate from the measured 100,000-image / 1,000,000-record inventory: bytes by class, small-object histogram, versions, growth, dump size/duration, seed/recovery bandwidth, API/KMS request counts, Glacier minimum-size/retrieval costs, ECR retention, and recovery compute/database hours. Use current Sydney pricing and an owner-approved monthly budget. Do not invent average photo sizes or present an unsupported dollar total.

### Release proof and drills

A signed recovery attestation must bind source revision, recovery template digest, API/worker/renderer and migration digests, primary/recovery aliases and regions, verified point/checkpoint signature, snapshot cutoff/LSN and acknowledged watermark, counts/bytes, hash/graph/XMP/metadata/audit/access checks, proof primary access was denied, isolated restore interval, achieved RPO/RTO, actual alert delivery, purge/retention assertions, and corrective actions.

Production release verification requires a full recovery qualification no older than three calendar months, matching the release's recovery-relevant image/infrastructure digests, RPO <=3,600 seconds, RTO <=86,400 seconds, and no unresolved blocking correction. A changed recovery-relevant artifact invalidates prior qualification. CI fixtures are not real evidence. Run a protected quarterly/manual drill with OIDC scoped only to recovery roles, no fork/untrusted branch access, no unattended promotion/purge, and encrypted recovery evidence.

Measure RTO from declared loss to complete core-data availability, validated owner login, and successful owner access. Use the approved maximum byte volume, not a small fixture.

## Planned files and verification

Planned paths are listed in [tasks.md](tasks.md): packages/recovery, apps/recovery-worker, apps/api/Database/Migrations/022_backup_recovery.sql, apps/api/Features/Recovery, packages/infrastructure/src/recovery/recovery-stack.ts, tests/recovery, tests/integration/recovery, tests/infrastructure/recovery, and .github/workflows/recovery-drill.yml. Operations and owner runbooks belong under docs/operations and docs/runbooks. API behavior is specified in [contracts/recovery.md](contracts/recovery.md), state/validation rules in [data-model.md](data-model.md), and runnable validation expectations in [quickstart.md](quickstart.md).

Local tests verify snapshot/ledger consistency, closure enumeration, hashing and receipt reuse, copy replay and purge fences, signatures, isolated restore, owner gates, failure alert rules, output privacy, and synthesized account/key/policy boundaries. Local tests prove software invariants only. AWS rehearsals separately prove actual account/identity boundaries, alert delivery, full seed and restore, measured volume, RPO/RTO, and cost.

## Rollout and approval

Deploy the recovery account stack and scoped source-read trust first. Seed all current payloads and release assets, complete full verification and isolated restore, then enable 15-minute capture/status. Monitor duration, closure lag, alert routing, size growth, and cost. Promotion and purge are separate, freshly MFA-approved recovery-account actions. Roll back code by disabling capture/status while retaining recovery copies, keys, and signed evidence.

T006 must record owner approval of: (1) the 15-minute pg_dump/exported-snapshot and recovery-controlled-copy design and release gate; (2) maximum media-byte/growth envelope and monthly budget; (3) Glacier Instant Retrieval after 90 days and warm evidence policy; (4) the recovery Cognito re-provision/password-reset path, mailbox availability, and independent MFA custody; (5) payload-only purge with retained historical hashes/paths/metadata or a separately specified erasure design; and (6) account trust, alert recipients, evidence retention, and release qualification freshness. Until T006 is complete, no implementation task may start.

The owner may approve these documented design assumptions for local implementation while deferring concrete account identities, byte/growth measurements, budget and recipient addresses to deployment qualification. Deferral does not waive a requirement or authorize an external action: T024/T026 remain blocked until the actual prerequisites and required evidence are available.

## Milestone checkpoints

M0 approves and commits artifacts/ADR. M1 commits shared schema/contracts/verification primitives. M2 commits complete capture/copy/health infrastructure and owner view. M3 commits restore, promotion, retention, purge and export acknowledgment. M4 commits independent regional recovery, runbooks and drill tooling/evidence. M5 commits acceptance, release gating, convergence and review remediation; the final review record receives its dedicated unchanged checkpoint after independent agreement. Each checkpoint includes its tests, affected documentation and numbered task state.


## Owner approval

Approved on 2026-10-05 by the product owner: proceed with this design and its numbered tasks, including media-only purge retaining historical paths/descriptive metadata, independent recovery identity/key custody, 15-minute coordinated capture and recovery-controlled copies, Glacier Instant Retrieval after 90 days, validation and release gates. Concrete workload/budget/account/recipient inputs are deferred to external qualification as permitted above. This approval authorizes local implementation, validation, milestone commits and remediation, but no external deployment, live restore/purge, push or merge.
