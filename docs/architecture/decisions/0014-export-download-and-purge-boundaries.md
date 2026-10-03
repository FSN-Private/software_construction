# ADR-0014: Dedicated export downloads and explicit purge dependency

- Status: Accepted (maintainer approved 2026-10-04)
- Date: 2026-10-03
- Owner: Project maintainer
- Feature: [012](../../features/012-versioned-export-basket/plan.md)
- Related: ADR-0004 immutable export snapshots; [planning review](../../features/012-versioned-export-basket/reviews/plan-tasks-review.md)

## Context and decision drivers

The existing HTTP API Gateway cannot return the proposed large ZIPs: [AWS HTTP API quotas](https://docs.aws.amazon.com/apigateway/latest/developerguide/http-api-quotas.html) list a 10 MB payload limit and 30-second integration timeout. Native downloads must support Chrome, Edge and Firefox without GiB JavaScript buffers while preserving cancellation/expiry/purge checks. Feature 005 explicitly defers permanent purge to unmerged 011; there is no existing purge producer to integrate.

## Proposed decision

Keep control APIs on HTTP API Gateway. Add HTTPS CloudFront/private-ALB routing to the existing API task for package streaming only, caching disabled, with server-side revocable owner-session grants and a one-use native-navigation cookie exchange. Exact source/object-version and range checks remain at the API, not edge caches. CloudFront [VPC origins](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/private-content-vpc-origins.html) support private ALBs in the existing Singapore region; their subnet/ENI/security requirements must be checked in infrastructure validation. [Origin settings](https://docs.aws.amazon.com/AmazonCloudFront/latest/DeveloperGuide/DownloadDistValuesOrigin.html) distinguish idle/read timeout from total completion timeout: do not cap a progressing large transfer with a total completion deadline.

Define a durable scoped tombstone and transactional export invalidation/cleanup acknowledgment contract. Feature 011 remains purge authority; feature 012 supplies only its consumer and export cleanup. Actual 011 publisher integration/evidence is a completion and production-enable prerequisite, not part of 012's authorized backup scope. See [handoff contract](../../features/012-versioned-export-basket/contracts/purge-invalidation.md).

Register durable intents before all export-owned storage writes. Invalidation closes admission; acknowledgment additionally requires proven settlement of admitted writers and reconciliation/deletion of all their destination versions/multipart remnants. DB publication fencing, expired leases or timeouts alone cannot prove cleanup. Unknown outcomes stay draining with alerts until settled; shared renderer objects retain their own lifecycle authority. T030 tests late uploads and lost callbacks across tombstone/cleanup.

Export metadata policy is a separately versioned transform on an existing approved renderer profile, recorded in manifest/cache identity. Do not silently introduce multiple profiles into the single-profile compatibility matrix.

## Alternatives and tradeoffs

- Existing Gateway stream: cannot meet package size; rejected.
- Native S3 presigned link: cannot perform new request-time lifecycle/session revocation checks; rejected.
- Chunked JS assembler: adds protocol and disk/browser compatibility complexity; rejected for initial native-browser delivery.
- Dedicated public service: unnecessary new runtime; reuse current API with narrowly routed private load balancer.
- Assume/implement purge in 005: contradicts its approved scope and bypasses recovery policy; rejected.
- Consumer fixtures alone: useful integration contract tests but insufficient end-to-end purge acceptance; retain explicit dependency gate.

## Consequences, operations and security

Added ALB/CloudFront cost and infrastructure lifecycle must be budgeted. Download origin must forward credentials and disable caching/error caching/access-log secrets; API/task security groups and listener paths limit exposure. Native cookie exchange is short-lived, single-use, same owner/session/request scoped and checks web Origin. Runtime tests must exercise deployed transport, large files, memory bounds, resumed ranges, bootstrap replay/CSRF and revocation. Invalidation cannot erase unrelated deduplicated content/shared renditions. Cleanup failure blocks final purge acknowledgment.

## Rollout, rollback and follow-up

Deploy additive tables and disabled capability, then transport and consumer. Do not enable production or report full completion without feature 011 evidence (T029). Rollback disables grants/exchange/submissions, preserves tombstones/audit and runs required cleanup; never redirects to public media. No deployment, purge or 011 merge authorized. T001 accepts this proposed ADR only after maintainer approval; T027-T029 track transport/dependency design and evidence alongside T010/T013/T015/T017/T020/T024.
