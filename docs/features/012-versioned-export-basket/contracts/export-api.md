# Export API v1 (proposed)

Owner base /v1/libraries/{library}/exports. Bearer owner authorization and owner/library lookup on all routes; private/no-store and standard safe problem/operation IDs. Cross-owner and missing IDs have identical denial shape. Repository idempotency headers and mutation revisions apply.

| Method/suffix | Contract |
|---|---|
| GET/POST /baskets | List/create; server establishes one active default. |
| GET/PATCH/DELETE /baskets/{basket} | Inspect readiness/details, rename/default with revision, delete mutable basket while preserving request evidence. |
| POST /baskets/{basket}/items | Asset/edit and current/ref/commit selection plus explicit advanced context. Atomically pin exact identities. Working selection rejected. Repeated selections return distinct IDs. |
| DELETE /baskets/{basket}/items/{item} or /items | Remove/clear with revision; never alter lifecycle holds. |
| GET /presets and /limits | Versioned preset catalog/metadata behavior and configured capacity/duration/retention limits; manifest mandatory. |
| POST /baskets/{basket}/items/{item}/preview | Request/inspect the exact stored pin using approved renderer compatibility; recheck current eligibility before returning a private preview locator. |
| POST /baskets/{basket}/requests | Preset/revision/consent/warning acknowledgment; required idempotency key. 202 snapshot/request immediately; same fingerprint returns original, changed fingerprint conflicts. |
| GET /requests or /requests/{request} | Owner history/snapshot/progress, safe errors, retry guidance, expiry, package length/hash. List includes every retained request plus the newest 200 expired/cancelled/unavailable records; `?before={requestId}` pages older terminal history in stable creation-time/ID order, scoped to the same owner/library. |
| POST /requests/{request}/cancel | Immediate revocation; idempotent draining/terminal cancellation. |
| GET /requests/{request}/manifest | Owner-only versioned manifest; safe summary survives media expiry/purge. |
| POST /requests/{request}/download-grants | Owner bearer authorization; create request/session-scoped one-use bootstrap <=60 seconds, originating bearer-expiry limit and configured HTTPS download-origin exchange URL; denied when unavailable. Standard idempotency replay must reissue an exchange code after reauthorization, never return a consumed credential. |

The media route is on a dedicated HTTPS CloudFront/private-ALB origin, not HTTP API Gateway. Only these listener routes reach the existing API task:

| Method/path on download origin | Contract |
|---|---|
| POST /v1/libraries/{library}/exports/requests/{request}/download-session | Top-level native form with bootstrap in body; verify one-use credential, expected web Origin, owner/request/session/expiry/revocation and lifecycle. Issue Secure/HttpOnly/SameSite=Lax request-path-scoped cookie, lifetime <=5 minutes and original bearer expiry; 303 to credential-free download URL. Explicit exchange endpoint exemption from generic bearer/idempotency middleware: one-use atomic receipt supplies idempotency and scoped authentication instead. |
| GET/HEAD /v1/libraries/{library}/exports/requests/{request}/download | Authenticate cookie grant or explicit owner bearer diagnostic client; verify same owner/session/request and lifecycle/tombstones under shared lock on every admission. Native disk download with bounded server buffer, single range/If-Range, ETag, length, Content-Range, safe Content-Disposition and X-Content-SHA256. New range requests after grant expiry need fresh owner-authorized exchange. |

No body response through existing Gateway; no S3 URL or grant in query strings. No caching/error caching, no logging of query/body/cookies/Authorization, Referrer-Policy no-referrer. Sign-out revokes all grants/bootstrap codes for the originating session. Consume exchange code atomically; reject cross-origin/replayed/foreign-request exchange. Do not require third-party fetch cookies: use top-level navigation and native browser save. An admitted stream may complete after credential expiry; later transfers reauthorize. Browser pause/resume tests must exercise refreshed cookies on the same immutable URL in Chrome/Edge/Firefox.

Control CORS allows exact configured web Origin and Authorization, Content-Type, Idempotency-Key, If-Match. Diagnostic stream CORS allows Range/If-Range and exposes ETag, Content-Range, Content-Length, Content-Disposition, X-Content-SHA256. HEAD matches GET authorization. Disable response compression/transformations for immutable ZIP/range bytes. Unavailable download transport fails safely, never falls back to unsigned/public storage.

Operator /v1/exports/operations: GET aggregate status; POST pause/resume/drain/cancel-pending with operator role. No operator selection/manifest/media/failure details.

Safe machine reasons: committed_version_required, advanced_history_required, source_unavailable, selected_version_unavailable, permanently_unavailable, incompatible_renderer, metadata_confirmation_required, quota_exceeded, integrity_error, export_paused, cancelled, expired, retryable, idempotency_key_reused. Client localizes labels; identifiers/checksums/UTC stay locale-neutral.

Basket item `availability`, `reason` and `outputName` describe rendered exports and rendered-preview eligibility. Additive `originalAvailability`, `originalReason` and `originalOutputName` describe unchanged-original eligibility and the exact preset-specific output name independently of XMP availability. Clients select the appropriate fields for the selected preset; an available original does not imply that its rendered preview is available. These fields expose no storage locator.

Permanent unavailability requires the trusted durable tombstone in [purge-invalidation.md](purge-invalidation.md); missing bytes alone indicate unavailable/integrity error. Feature 011 publisher integration is T029's full acceptance/rollout dependency.
