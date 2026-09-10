# Version-control service operations and cost

Owner: platform operator. The API reads PostgreSQL directly, so ingest-worker degradation must not interrupt history reads. Alert when commit/ref availability falls below 99.9% over the rolling month, p95 asset-head reads exceed 200 ms, p95 10,000-node first pages exceed 2 seconds, or p95 100-entry commits exceed 2 seconds after content is authoritative.

Dashboard commit/ref command duration and rates with `platform.version_control.command.duration`, `platform.version_control.commits`, and `platform.version_control.ref_movements`; correlate `platform.version_control.stale_heads` and `platform.version_control.ref_conflicts` before retrying. Integrity verification, failure, projection rebuild, and recovery-expiry counters must remain active even while the ingest worker is degraded.

The owner-scoped `GET /v1/libraries/{library}/version-control/inventory` report separates unique authoritative XMP bytes, commit rows, entry rows, projection rows, audit rows, and recovery rows. Review monthly and before a capacity increase. Hashes and counts are permitted; XMP payloads, private paths, filenames, geolocation, and descriptive metadata are prohibited.

For cost anomalies, compare category deltas with commit/ref/recovery rates. Rebuild projections if row counts drift from graph state. Expire eligible recovery refs, but do not compact immutable commits or release lifecycle holds merely to reduce cost. Compliance retention and active holds override cleanup. Escalate unexplained 20% week-over-week growth or any content-without-hold condition.
