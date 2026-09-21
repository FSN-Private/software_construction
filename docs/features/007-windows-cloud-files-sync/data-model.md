# Windows Sync Data Model

| Entity | Invariant |
|---|---|
| Sync device/root | owner-scoped registration, selected branch/cursor, revocation/health; one independent checkout per device |
| Placeholder | opaque remote IDs, expected hash/size, path mapping, availability/pin/status; no credentials |
| Journal entry | monotonic sequence, base/current hash, operation, durability/ack/retry state; replayable/idempotent |
| Hydration | identity/range/progress/outcome; never completes before exact content verifies |
| Pending change | stable local XMP, expected base, idempotency ID; publishes only as working change |
| Conflict | base/local/remote identities, preserved local path/bytes, reason/resolution |
| Checkout | old/new branch, dirty decision, delta cursor, outcome |

`online-only → hydrating → available`; pin adds `pinned`; local edit creates `dirty → uploading → synchronized` or `conflicted`. Delete, restore, and dehydrate are separate journal operations.
