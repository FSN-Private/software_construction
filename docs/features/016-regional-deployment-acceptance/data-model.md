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
