# Windows Sync Contract v1

The provider exchanges opaque device/root/asset/edit/content IDs, expected base/version, idempotency key, cursor, operation ID, and safe state/error code. It never sends a credential or raw local journal.

| Operation | Required outcome |
|---|---|
| register/revoke root | independent device branch/cursor; revocation blocks remote access only |
| enumerate/delta | paged branch namespace and verified cursor; expiry requires reconciliation |
| hydrate/pin | exact identity/hash bytes; per-item recursive completion/error |
| working upload | stable XMP bytes + expected base; uncommitted working change or visible conflict |
| checkout | block/preserve/discard dirty state then apply branch delta |
| move/delete/restore | distinct audited expected-base operations; never confuse dehydration with deletion |
| status/diagnostics | online-only, hydrating, available, pinned, dirty, uploading, synced, paused, auth-required, quota/error, conflicted |
