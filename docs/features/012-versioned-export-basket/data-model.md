# Export data model v1

Proposed. All entities owner/library scoped; UUID IDs, lowercase SHA-256, UTC timestamps, versioned JSON. No basket/snapshot foreign key may block source purge or create retention holds.

| Entity | Fields and invariants |
|---|---|
| Basket | ID, owner/library, bounded name, revision, default flag, timestamps; one default/library; >=20 saved capacity. |
| Item | ID, basket, asset/edit, original SHA/length/version, commit, XMP SHA/length/version, safe basename, ref/version labels, explicit hidden selection context, timestamp. Duplicates have distinct IDs. Readiness computed from lifecycle/integrity. |
| Preset | Immutable ID/version, output format/size/color, metadata policy/consent and approved compatibility. No client renderer arguments. |
| Snapshot/request | ID, owner/library/basket, idempotency key/fingerprint, schema, preset/consent/warning acknowledgment, immutable copied items/compatibility, status, lease owner/generation/deadline, timestamps/expiry. Survives basket deletion. |
| Result | Request/item IDs, pending/completed/failed/unavailable/skipped/cancelled, safe reason, deterministic filename, checksum/length, publication version and exact provenance. |
| Package | Request, private object/version, checksum/length, manifest schema/checksum/summary, publication/expiry/revocation; one eligible package/request. |
| Storage write intent | Owner/library/request/attempt generation, unique export-owned destination, category, writer/admission state, multipart IDs, observed versions and proven settlement/cleanup state; registered before every upload. Unknown storage outcome prevents cleanup acknowledgment even when the lease expires. |
| Grant/bootstrap | Hashed one-use <=60-second bootstrap, owner/request/session and original bearer expiry; exchange yields hashed <=5-minute request-path cookie grant with revocation; no URL/log secrets. |
| Purge tombstone/outbox | Immutable schema/event ID, owner/library/logical asset/edit/content/version scope, authorized operation/evidence, monotonic lifecycle generation/time; export cleanup job/version list and acknowledgment. Trusted 011 producer only; consumer fixtures do not prove policy execution. |
| Audit/operations | Actor/role, operation/request/basket IDs, safe action/reason/counts/UTC, aggregate metrics and pause/drain/cancel flags; no descriptive/private payload. |

Transitions: pending -> preparing -> completed/partial/failed; pending/preparing -> cancelling -> cancelled; completed/partial -> expired/unavailable-on-purge. Lease loss -> retryable/bounded retry or terminal failure within ten minutes. Pause blocks new submission/claims; drain lets current claims finish. Cancel/purge fences publication and grants before cleanup.

Manifest schema 1 records every requested item/status/reason, output name/checksum/length for completion, exact source/edit/XMP/commit context, preset/metadata treatment, preparation time, render provenance plus metadata-transform ID/version and input/output hashes, or explicit original-only provenance. Exclude private paths/XMP/metadata/secrets. Reject unsupported versions safely; tolerate additive optional fields. Package hash returned separately.


Shared owner/library lifecycle lock serializes tombstone publication with export eligibility/publication/download admission. Cleanup acknowledgment concerns exact export object versions, not shared renderer caches. Feature 011 owns purge authority and original/recovery deletion; T029 gates real purge acceptance and rollout. See contracts/purge-invalidation.md.
