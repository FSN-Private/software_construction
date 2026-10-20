# T054 cache-latency acceptance transport decision

Status: owner approved option1 on2026-10-20 ("Approve option 1: persistent HTTP/2"). Implementation and independent/live validation pending. Primary nonproduction only. No threshold, concurrency, resource or qualification waiver.

Deployment39390207398 passed all5actual uncached completions within30seconds, then failed cache-hit p95 at1582.3ms against500ms. Rollback restored stopped API6/ingest4/renderer1.

## Server remediation already under validation

Bound concurrent cached-locator queries to4using a singleton SemaphoreSlim. Retain every owner/library/completed-state SQL predicate, fresh per-request authorization query, exact object version/provenance, existing5minute private URL and CPU/memory allocation. Queue time remains included in request latency. Local quarter-CPU/2GiB before/after retained probe has cold p95412.2275ms/warm15.4724ms before and207.3548ms/warm95.7907ms after. An earlier transient run reached886.7807ms cold/91.283ms warm (tool transcript only). Different cold runs vary; none are cloud qualification. Local PG is non-TLS and AWS credentials synthetic.

## Transport evidence and options

With the actual API stopped,20concurrent public GET/live requests through the existing fetch client after one warm request take620?681ms each and return gateway500. These failures are diagnostic transport baselines, never healthy acceptance. The same20requests over a warmed standard-library HTTP/2 session take200?396ms. These are local-client observations, not proof of GitHub-runner latency or successful API responses. They show why server optimization alone may not satisfy the current cold-HTTP/1.1 burst measurement.

1. Use a persistent HTTP/2 session for the rendering acceptance client, matching a browser connection. Keep5distinct uncached jobs, all20concurrent cached requests, the same endpoint/authentication,2s request-start/30s completion/500ms cached p95 limits, complete-response validation, TLS certificate verification and fail-closed status handling. All rendering requests share the session from initial POST through polling/cache, so the connection is established by the actual preceding workload. No extra warmup requests or removal of cold application/database work. Use node:http2 only, no dependency/IAM/resource change. Require real local HTTP/2 client tests, updated contract tests, independent review and new trusted/live acceptance. This deliberately changes transport assumptions and needs owner approval of the nominal-client interpretation.
2. Retain the existing HTTP/1.1 client and its connection-startup timing. Investigate a same-region acceptance runner or another deployment topology that meets the unchanged end-to-end500ms limit. No unapproved provisioning or changed performance region is implemented; runner/cost/security design would require separate review.

Neither option accepts the failed run, qualifies its fixtures, relaxes500ms, changes approved image references, resolvesT048/startup500, or claims DR qualification. Approval authorizes implementing the selected transport design and the usual review/CI/deployment gates, not bypassing them.
