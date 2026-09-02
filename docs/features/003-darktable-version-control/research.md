# Research: Darktable Version-Controlled Image Library

**Feature**: [spec.md](spec.md)
**Date**: 2026-09-02
**Status**: Complete for planning; implementation must re-verify pinned versions and regional availability.

## Decision 1: Use a modular monolith with isolated asynchronous workers

**Decision**: Build one ASP.NET Core API/domain application, one React web client, one Windows sync client, and two independently deployable worker processes (ingest/index and darktable render). Keep version-control, library, metadata, sync, rendering, recovery, and audit as explicit modules with internal interfaces. Run the API and workers as separate containers, but keep one repository and one transactional PostgreSQL data model.

**Rationale**: The initial product is single-user and only targets ten concurrent browser sessions, while commit atomicity, branch compare-and-swap, audit, and metadata consistency need strong transactions. A modular monolith avoids distributed transactions and premature service boundaries; separate workers contain native media processing and scale independently.

**Alternatives considered**:

- Fine-grained microservices: rejected because the operational and consistency cost is disproportionate to v1 scale.
- Fully serverless functions: rejected because darktable is a large native process with variable execution time, and workflow/history transactions benefit from a long-running application boundary.
- One process for API and rendering: rejected because malformed media or a darktable crash must not affect request handling.

## Decision 2: Standardize on supported LTS runtimes

**Decision**:

- API, domain, workers, and Windows client controller: .NET 10 LTS / C# 14.
- Web client and AWS CDK: Node.js 24 LTS, TypeScript 6, React 19.2.
- Windows filesystem boundary: direct Cloud Files API interop from the .NET Windows client, isolated behind a native-interface adapter and verified against a dedicated Windows 11 test host.
- Database: PostgreSQL 18 at its current security patch, provisioned through Amazon RDS.

**Rationale**: .NET 10 is active LTS through November 2028 and covers ASP.NET Core plus Windows desktop code. Node 24 is LTS; React 19.2 is the current stable line; TypeScript 6 is current and makes deprecated options explicit. PostgreSQL 18 is supported through November 2030 and is available on RDS. Shared C# domain/sync models reduce protocol drift, while TypeScript remains appropriate for the browser and CDK.

**Evidence**: [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy), [Node.js release schedule](https://nodejs.org/en/about/previous-releases), [React versions](https://react.dev/versions), [TypeScript 6 release notes](https://www.typescriptlang.org/docs/handbook/release-notes/typescript-6-0.html), and [PostgreSQL version policy](https://www.postgresql.org/support/versioning/).

**Alternatives considered**:

- All-TypeScript backend: rejected because the Windows Cloud Files integration and long-lived server support are stronger with .NET, while sharing web types can be generated from OpenAPI.
- Native C++ Windows application: rejected for v1 because it increases memory-safety and development risk; native calls remain isolated and can move to a C++ helper only if interop tests prove necessary.
- Rust throughout: rejected because it adds framework and Windows integration risk without a demonstrated need at this scale.

## Decision 3: Support darktable 5.6.x and 5.4.x with XMP as the interchange authority

**Decision**: The initial compatibility matrix covers darktable 5.6.x (server baseline 5.6.1) and 5.4.x. The render image is built from the official 5.6.1 source archive and verified against the published SHA-256 checksum; a 5.4 compatibility lane verifies round trips and representative renders. The server invokes `darktable-cli <image> <xmp> <output>` with a private temporary directory, `--library :memory:`, fixed output settings, disabled network access, resource limits, and no user presets or downloaded AI models.

**Rationale**: The specification requires the two most recent stable release lines. As of the plan date, the current stable line is 5.6.1 and the previous line is 5.4.x. `darktable-cli` is explicitly designed for headless/background export and accepts a chosen XMP sidecar. darktable itself warns that newer library/configuration state may not be backward-compatible, so the service must never share its database with desktop clients.

**Evidence**: [darktable 5.6.1 release and checksums](https://www.darktable.org/2026/08/darktable-5.6.1-released/), [darktable 5.4 release](https://www.darktable.org/2025/12/darktable-5.4.0-released/), [`darktable-cli` invocation](https://docs.darktable.org/usermanual/development/en/special-topics/program-invocation/darktable-cli/), [XMP storage behavior](https://docs.darktable.org/usermanual/development/de/preferences-settings/storage/), and [multiple-computer limitations](https://docs.darktable.org/usermanual/development/en/special-topics/multiple-computers/).

**Alternatives considered**:

- Track nightly darktable: rejected because previews and XMP behavior would not be supportable or reproducible.
- Synchronize `library.db`: rejected because darktable does not provide multi-computer conflict resolution and database versions can be incompatible.
- Render through a reimplemented pipeline: rejected because it cannot guarantee darktable semantics.

## Decision 4: Use a content-addressed custom commit DAG, not Git repositories

**Decision**: Store original images and byte-exact XMP revisions as immutable SHA-256-addressed objects. Store commits, parents, changed image/version entries, branches, tags, reflog entries, visibility preferences, and materialized branch state in PostgreSQL. Compute a commit ID from a canonical versioned manifest containing library ID, ordered parents, ordered changes, author, authored time, and message. Advance branch heads with a serializable transaction and expected-head compare-and-swap.

**Rationale**: Git semantics are valuable, but placing large images in Git or maintaining one filesystem repository per library conflicts with direct object upload, transactional metadata search, retention, and recovery. A purpose-built immutable DAG gives stable lineage and atomic multi-image commits without exposing Git's storage or protocol as a public contract.

**Alternatives considered**:

- A bare Git repository with XMP only: rejected because database/search state and object lifecycle would still require a second source of truth, and library-wide path operations would be expensive at 100,000 images.
- Git LFS for images: rejected because the product is not a Git host and LFS adds pointer/server semantics unrelated to the Windows placeholder contract.
- Mutable revision rows: rejected because they weaken integrity, audit, and content-addressed recovery.

## Decision 5: Merge XMP conservatively with a versioned semantic policy

**Decision**: Parse XMP namespace-aware, preserve every input byte stream as an immutable revision, and generate a new deterministic XMP document only for a resolved merge. Auto-merge only changes proven disjoint by the three-way base/ours/theirs comparison:

- set-like tags merge by independent additions/removals;
- scalar descriptive fields merge only when one side is unchanged from base;
- rating, rejection, color labels, title, description, creator, and geolocation conflict if both sides changed differently;
- darktable processing history, module order, masks, blend parameters, duplicate/version identity, and unknown namespaces conflict whenever both sides differ;
- deletion versus modification always conflicts.

The UI must preview the semantic diff and darktable-rendered result before committing an automatic merge. The policy has an explicit version stored with each merge commit.

**Rationale**: XML text merging can produce syntactically valid but visually wrong edits. Conservative conflicts preserve both sides and satisfy the no-silent-overwrite requirement.

**Alternatives considered**:

- Line-based XML merge: rejected as unsafe.
- Last-writer-wins: rejected because it loses edits.
- Never auto-merge: safe but unnecessarily burdens disjoint tag and descriptive-metadata changes.

## Decision 6: Implement Windows placeholders with the Cloud Files API

**Decision**: Register one sync root per library/device through `CfRegisterSyncRoot`; represent online-only items as placeholders, hydrate via range-aware callbacks, and use platform pin states for file/folder offline availability. A per-device local SQLite journal records server cursor, checked-out branch, placeholder identity, content hash, local write sequence, upload state, and conflicts. The provider uploads changes only after an exclusive-read stability window and uses expected remote/base hashes to prevent overwrite.

**Rationale**: The Cloud Files API is the Windows platform contract for OneDrive-style placeholders, pinning, hydration, and Explorer status. Microsoft documents sync-root registration, hydration/population policies, pin states, and asynchronous dehydration. Stable opaque file identity binds a placeholder to library, asset, branch, and revision rather than to a mutable path.

**Evidence**: [Cloud Files API](https://learn.microsoft.com/en-us/windows/win32/api/_cloudapi/), [`CfRegisterSyncRoot`](https://learn.microsoft.com/en-us/windows/win32/api/cfapi/nf-cfapi-cfregistersyncroot), [cloud storage provider integration](https://learn.microsoft.com/en-us/windows/win32/shell/integrate-cloud-storage), and Microsoft's [CloudMirror sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/cloudmirror-sample/).

**Alternatives considered**:

- WebDAV/network drive: rejected because it cannot provide the required placeholder and pin-state experience and is fragile for darktable writes.
- Full mirror only: rejected because a 100,000-image library may exceed local disk capacity.
- Filesystem filter driver: rejected because kernel-mode code and signing add unnecessary security and maintenance risk.

## Decision 7: Use PostgreSQL for searchable metadata and S3 for immutable content

**Decision**:

- Amazon RDS for PostgreSQL 18 Multi-AZ DB instance for transactional graph, normalized metadata, full-text search, range filters, sync cursors, job state, and audit indexes.
- S3 Standard, versioning, SSE-KMS, blocked public access, and checksum validation for primary originals, XMP objects, canonical commit manifests, and recovery journals.
- A separate S3 bucket/prefix for regenerable previews with lifecycle expiration; previews never become recovery authorities.
- PostgreSQL B-tree, GIN, GiST, generated columns, and keyset pagination for v1 search. Add OpenSearch only if measured 100,000-image tests cannot meet NFR-001.

**Rationale**: The reference scale is well within PostgreSQL's indexed-query capability, and avoiding an external search engine eliminates eventual-consistency and rebuild complexity. S3 is the natural immutable large-object boundary.

**Alternatives considered**:

- OpenSearch from day one: rejected as premature cost and operational complexity.
- Store XMP and images as database large objects: rejected because it impairs streaming, replication, and lifecycle management.
- DynamoDB for commit graph and metadata: rejected because multi-row commit/ref transactions and flexible filters fit PostgreSQL better.

## Decision 8: Deploy on AWS using managed boundaries

**Decision**:

- Default primary region: `ap-southeast-1`; recovery region: `ap-southeast-2`. Region availability is revalidated before deployment.
- Separate AWS accounts for non-production, production, and recovery.
- S3 + CloudFront for the web client; Cognito User Pool authorization-code/PKCE for the single provisioned owner.
- Application Load Balancer to ECS Fargate API tasks across at least two availability zones.
- ECS Fargate ingest/index and render workers consume SQS queues with dead-letter queues; render workers use CPU/memory/ephemeral-storage limits and no public network route.
- RDS PostgreSQL Multi-AZ, Secrets Manager, KMS, ECR, CloudWatch/OpenTelemetry, AWS WAF, and AWS CDK v2 infrastructure.

**Rationale**: Managed services meet the availability and operational targets without Kubernetes. ECS separates the darktable native process while allowing a warm worker to satisfy preview latency. Separate accounts and private subnets reduce the blast radius.

**Evidence**: AWS recommends spreading ECS services across availability zones, and RDS PostgreSQL supports Multi-AZ deployments ([ECS availability-zone balancing](https://docs.aws.amazon.com/AmazonECS/latest/developerguide/service-rebalancing.html), [RDS PostgreSQL](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/CHAP_PostgreSQL.html)). [AWS CDK best practices](https://docs.aws.amazon.com/cdk/v2/guide/best-practices.html) support a single application package and environment separation.

**Alternatives considered**:

- EKS: rejected because Kubernetes is not justified at this scale.
- Lambda for API and render: rejected because native render packaging/execution and predictable warm latency are worse fits.
- Publicly reachable database or workers: rejected on security grounds.

## Decision 9: Use cross-account, cross-region cold recovery

**Decision**:

- Enable S3 versioning and S3 Replication Time Control from production to a recovery-account bucket in `ap-southeast-2`, with destination ownership, independent KMS keys, Object Lock governance, no delete-marker replication, and direct/lifecycle transition to S3 Glacier Deep Archive.
- Replicate canonical commit manifests, immutable objects, recovery journals, audit exports, and integrity inventories; monitor replication-threshold events.
- Copy hourly RDS backups cross-account/cross-region and export a versioned reconstruction journal to replicated S3 so the 1-hour RPO does not depend on one database snapshot mechanism.
- Replicate ECR release images and retain CDK assemblies plus attested release manifests.
- Run quarterly isolated restores in the recovery account; never restore destructively over production.

**Rationale**: S3 RTC has a 15-minute SLA for 99.99% of new objects, cross-account destination ownership resists source-account compromise, and Deep Archive standard retrieval is designed to complete within 12 hours, leaving time within the 24-hour RTO for reconstruction and validation.

**Evidence**: [S3 replication and RTC](https://docs.aws.amazon.com/AmazonS3/latest/userguide/replication.html), [cross-account replication behavior](https://docs.aws.amazon.com/AmazonS3/latest/userguide/replication-walkthrough-2.html), [Object Lock replication](https://docs.aws.amazon.com/AmazonS3/latest/userguide/object-lock-managing.html), [RDS cross-region automated backups](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/AutomatedBackups.Replicating.Enable.html), and [S3 Glacier Deep Archive retrieval](https://aws.amazon.com/s3/faqs/).

**Alternatives considered**:

- Same-account recovery: rejected because compromised credentials could reach both copies.
- Same-region backup: rejected by the approved regional-loss requirement.
- Active-active application regions: rejected because the approved strategy is backup/restore and does not justify continuous multi-region database complexity.

## Decision 10: Deliver through GitHub Actions with immutable promotion

**Decision**:

- `ci.yml`: pull requests and protected-branch pushes; stable required checks `ci / backend`, `ci / web`, `ci / windows`, `ci / infrastructure`, `ci / security`, and aggregate `ci / required`.
- `build-release.yml`: reusable, protected-default-branch-only build of API/worker containers, web bundle, CDK assembly, and Windows MSIX; generate checksums, SPDX SBOMs, and GitHub artifact attestations.
- `deploy-nonprod.yml`: automatic after a successful protected-default build; verify attestations and deploy the exact digests through GitHub OIDC.
- `release.yml`: `v*` tag matching the built revision, protected `production` environment approval, attestation verification, database compatibility gate, and promotion of the same digests.
- `rollback.yml`: environment-dispatched rollback to a named compatible release manifest; production requires approval.
- `recovery-drill.yml`: scheduled/manual isolated recovery validation with no production mutation.

Top-level token permission is `contents: read`; jobs add only the permissions they require. Deployment trust is scoped to immutable repository identity, workflow, ref/environment, and audience. No `pull_request_target` workflow checks out untrusted code. Third-party actions are pinned to reviewed full commit SHAs with version comments.

**Rationale**: GitHub OIDC avoids long-lived AWS credentials, protected environments gate production, and artifact attestations bind artifacts to source/workflow evidence. Building once prevents environment-specific drift.

**Evidence**: [GitHub OIDC for AWS](https://docs.github.com/en/actions/how-tos/secure-your-work/security-harden-deployments/oidc-in-aws), [artifact attestations](https://docs.github.com/en/actions/concepts/security/artifact-attestations), [attestation verification and SBOMs](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations), and [GitHub Actions billing](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

**Alternatives considered**:

- Long-lived AWS access keys: rejected.
- Rebuild during deployment: rejected because provenance and rollback would be ambiguous.
- Automatic production deployment on every default-branch merge: rejected because the approved specification requires versioning and protected approval.
- CDK Pipelines as the delivery orchestrator: rejected because the user explicitly selected GitHub Actions; CDK remains the infrastructure definition.

## Decision 11: Use backward-compatible migrations and forward recovery

**Decision**: Apply expand/migrate/contract database changes. A release may add nullable structures and dual-read/write paths first; backfills are resumable jobs; destructive constraints or column removal occur only in a later release after compatibility telemetry passes. Application rollback is permitted only while the previous version remains schema-compatible. Otherwise the release pauses and uses an approved forward-recovery migration.

**Rationale**: Reverting application containers cannot safely reverse user writes or destructive schema transforms. This pattern preserves the specification's data-safety guarantee.

**Alternatives considered**:

- Down migrations as the default rollback: rejected because they can destroy newly written data.
- Maintenance-window big-bang migration: rejected because it weakens availability and rollback.

## Decision 12: Establish a risk-proportional dependency gate

**Decision**: Commit lockfiles for NuGet, npm, and CDK; use central dependency declarations where supported; prohibit floating package and action versions; verify darktable archives by published checksum; produce SPDX SBOMs for each release unit; run license, vulnerability, dependency-review, secret, IaC, and container scans; and block exploitable critical/high findings. Renovation automation may open narrowly scoped updates but cannot auto-merge production dependencies.

**Rationale**: The renderer, sync provider, CI actions, and deployment tools are privileged supply-chain boundaries. Pinning plus provenance makes updates reviewable and rollback attributable.

**Alternatives considered**:

- Broad automatic dependency upgrades: rejected because it mixes unrelated risk and can invalidate darktable compatibility.
- Unpinned marketplace actions: rejected because tags are mutable.

## Decision 13: Preliminary reference cost envelope

**Decision**: Use a planning envelope of **USD 450–950/month** for one production library before support plans, taxes, exceptional egress, or disaster restores. The estimate assumes 100,000 images averaging 30 MB (about 3 TB originals), 100 GB of XMP/history objects, 200 GB previews, 200 GB monthly internet egress, one Multi-AZ burstable RDS instance, two small always-on API tasks, one warm render worker plus burst capacity, cross-region replication, Deep Archive recovery storage, logs, queues, load balancer/NAT, and 5,000 GitHub-hosted CI minutes split between Linux and Windows. Non-production is scheduled down when idle and is expected to add USD 100–250/month.

Before production approval, generate and attach an AWS Pricing Calculator estimate using the final region, measured average object size, render duty cycle, RDS class, NAT/egress path, and retention; configure AWS Budgets at 80% and 100% of the approved amount.

**Rationale**: Storage is material but the always-on Multi-AZ database, networking, and compute dominate the baseline. Deep Archive begins around USD 0.00099/GB-month in lower-cost regions but has 180-day minimum storage and retrieval charges; GitHub runner minutes and artifact storage are metered beyond plan allowances. A range is more honest than a false point estimate before workload measurement.

**Evidence**: [AWS S3 pricing and archive characteristics](https://aws.amazon.com/s3/faqs/), [RDS PostgreSQL pricing model](https://aws.amazon.com/rds/postgresql/pricing/), [Fargate pricing model](https://aws.amazon.com/fargate/pricing/), and [GitHub Actions billing rates](https://docs.github.com/en/billing/concepts/product-billing/github-actions).

**Alternatives considered**:

- OpenSearch, EKS, active-active regions, and always-on large render fleets: rejected for v1 because each materially raises the baseline without evidence of need.
- Promise a fixed monthly price before measurements: rejected because image size, render frequency, egress, and region choice dominate cost.

## Resolved Unknowns

All technical-context unknowns from the approved specification are resolved for Phase 1. Implementation must verify current patch versions, third-party action commit SHAs, exact AWS prices, service quotas, region availability, and production code-signing custody before enabling external deployment; those are validation gates rather than design ambiguities.
