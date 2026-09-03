# Ingest and Storage Data Model

| Entity | Required fields | State / invariants |
|---|---|---|
| Import batch | ID, owner, idempotency key, atomic flag, counts, outcome | created → processing → completed/failed; each item has a durable result |
| Upload session | ID, item, expected length/SHA-256, expiry, received parts, lifecycle version | resumable; bound to owner and target intent; duplicate completion returns original result |
| Content object | SHA-256, length, category, storage key/version, integrity state | immutable; exact bytes; one authoritative content object per digest |
| Image asset | stable UUID, original content ID, lifecycle version/state | path/device/upload independent; replacement bytes need an explicit new-asset decision |
| Edit identity/XMP | stable edit ID, source asset, duplicate ordinal, content ID, association provenance | one source asset per edit; XMP bytes unchanged; no association means quarantine |
| Path record | asset ID, normalized collision key, display path, effective range | immutable history; one active collision key per library; no silent overwrite |
| Publication intent | ID, content/metadata set, object state, retry count, repair evidence | pending → completed/reconciled/failed; never acknowledge unreachable content |
| Lifecycle hold | ID, object/asset, reason, source reference, expiry | blocks physical removal |
| Reconstruction manifest | version, inventory digest, object/asset/path/hold/audit/handoff records | canonical and checksummed; detects altered relationships |

Relationships: batch has items/sessions; sessions produce content; assets reference original content and paths; edit identities reference an asset and optional XMP content; publication intents connect database and object-store state; manifests inventory all authoritative records.
