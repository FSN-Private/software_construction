# Data model and lifecycle

No database changes. Release manifest adds an attributable client zip subject.
Request contains schema1, unique invocation ID, full sourceSHA, releaseRunId,
manifestSHA, bundleSHA, configured public origin, five distinct fixture triples
and one transient owner token. Only the token is secret; it is never retained.
Result echoes binding fields plus actual region, process/runtime identity,
qualified functionARN and safe rendering/ingest results or failed stage/metrics.
Validated safe proof joins existing deployment evidence; all samples are kept.
Payload/result temp files are removed in finally. Client logs retain7days.
Published versions are preserved for attributable rollback; no user assets or
credentials are retained in client infrastructure.

Review remediation adds operational rollback receipts in existing render_events
reason_code JSON under reserved rollback:<matrix> audit library markers. Receipts
contain server-read prior approval fields and configured candidate/evidence, not
owner tokens or user content. Admission returns only the receipt UUID. No new
schema, dependency, key store, application content or data permission. Latest
matrix receipt and exact current state guard replay; rollback restores approvals
and closes intake transactionally before task replacement.
