# Version-Control Contract v1

Mutating operations require idempotency key and expected ref/working-state version. All responses include operation ID and safe machine error code.

| Operation | Required result |
|---|---|
| working/staging | list/create/update/stage/unstage with base/current XMP identities |
| commit | canonical commit ID or prior idempotent result; stale head returns base/ours/theirs without loss |
| refs/tags | create/list/rename/delete/resolve exact commit; protected-ref violation is rejected/audited |
| compare/divergence | changed asset/path/lifecycle/XMP IDs and exact base/ours/theirs |
| checkout | clean switch, block, preserve/stash, or explicit confirmed discard dirty state |
| rewrite/recovery | preview, confirmed ref-only move, reflog/recovery reference, recovery by named ref |
| visibility/projection | reversible hide/unhide and paged whole/image projection with show-hidden option |
| integrity | manifest/object/parent/cycle verification and rebuild status |

Errors: `stale_head`, `protected_ref`, `invalid_ref_name`, `ref_conflict`, `checkout_conflict`, `dirty_checkout`, `content_not_authoritative`, `cross_library_reference`, `graph_corrupt`, `unresolved_divergence`, `retention_hold`, and `version_control_unavailable`.

## HTTP surface

All routes are below `/v1/libraries/{library}/version-control`, require an authenticated owner, and return the platform operation ID. Mutations require `Idempotency-Key`; client-scoped operations also require `X-Client-Id`, and versioned staging/ref/checkout mutations require `If-Match` through the platform lifecycle-version contract.

| Method and route | Contract |
|---|---|
| `GET/PUT /working[/{branch}]` | `WorkingChangeRequest` and ordered `WorkingChangeView` values |
| `POST /staging/{branch}`, `DELETE /staging/{set}/entries/{working}` | `StageRequest`; exact working versions prevent staged-set drift |
| `POST /commits` | `CommitRequest`; atomic single-parent commit/ref/reflog/hold/idempotency result; stale errors include redacted base/ours/theirs identities |
| `GET /refs`, `POST /refs/{branch|tag}`, `PATCH/DELETE /refs/{name}` | `RefRequest` and exact `RefView` resolution |
| `GET /graph/{ref}`, `GET /compare?ours=&theirs=` | paged whole/image lineage and exact `DivergenceChangeView` base/ours/theirs values |
| `POST /checkout`, `POST /restore` | explicit dirty action; restore/revert produces a new commit |
| `GET /rewrite/preview`, `POST /rewrite`, `POST /recovery/{reflog}` | preview-token-bound ref move and named recovery |
| `PUT /visibility` | reversible presentation-only hidden metadata |
| `POST /merges` | resolver-supplied entries, recorded base/source/target, ordered two-parent atomic publication |
| `POST /integrity/verify`, `POST /projections/rebuild` | quarantine-on-failure verification and derived-state rebuild |
| `GET /state/{ref}/{asset}`, `GET /inventory` | indexed asset state and owner-scoped cost categories |

Canonical commit manifests use contract version 1. Readers reject unknown versions, IDs are lowercase SHA-256 digests of exact canonical bytes, entries are ordered and unique per asset/edit identity, and parent order is significant (`target`, then `source` for merges). Error bodies use safe machine codes and plain-language titles and never contain XMP, paths, filenames, geolocation, or descriptive metadata.
