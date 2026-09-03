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

Errors: `stale_head`, `protected_ref`, `invalid_ref_name`, `ref_conflict`, `dirty_checkout`, `content_not_authoritative`, `cross_library_reference`, `graph_corrupt`, `unresolved_divergence`, and `retention_hold`.
