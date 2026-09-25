# Rendering Data Model

| Entity | Immutable fields | Mutable/state fields | Rules |
|---|---|---|---|
| Render request | `id`, owner/library, request key, asset identity, selected edit/version identity, rendition, profile, renderer/matrix version | `state`, latest attempt, created/updated timestamps | One active request per owner, library, and request key; completed requests are reused only on exact key match. |
| Render attempt | `id`, request, attempt number, input identities, worker policy version | queued/running/completed/failed/cancelled/retryable state, timestamps, safe category | Retry creates a new attempt; terminal rows never change input identity. |
| Rendition | `id`, request key, output SHA-256, byte length, type, private object version | completion time, retention eligibility | Exists only with matching provenance. |
| Preview provenance | rendition, source SHA-256, XMP SHA-256, selected version, renderer digest/release, profile/matrix version | completion time | Stored atomically with a rendition; fields are language-neutral. |
| Compatibility matrix entry | release/profile/matrix version, approved artifact digest, corpus evidence identifier | status and approval time | New renderer/profile use requires an explicit approved entry. |

## State transitions

```text
pending -> running -> completed
       \-> unavailable | failed -> retryable -> pending
pending/running -> cancelled
```

`completed` requires a validated private derivative and a matching provenance row. `unavailable` has no derivative. A process restart converts a stale `running` attempt to `retryable` or a safe terminal failure within ten minutes.
