# Export basket operations

Feature 012 capability defaults to disabled (`Exports:Enabled=false`). Schema 021 is additive and is included in the migration artifact; basket/source identities and prior readers remain intact. Baskets never acquire or release original/XMP/history holds. Approval of the design does not enable deployment.

Owner-scoped baskets support 20 saved baskets, 250 pinned selections, revisions, a default basket and explicit duplicates. Selection resolves current/main, branch/tag or exact committed history once; mutable working selections are rejected. Hidden selection requires explicit advanced history. Missing bytes are unavailable, never proof of permanent purge.

Feature 011 integration uses the trusted in-process durable tombstone consumer described in feature 012 contracts/purge-invalidation.md. There is no public tombstone publisher. Original versus XMP and logical asset/edit scope preserve other retained deduplicated images and original-only exports. Do not acknowledge export cleanup while admitted or unknown writes remain; lease expiry is not proof of writer quiescence. T029 gates complete acceptance and production enablement on real feature 011 purge evidence.

Further preparation/download/cleanup/operator runbooks are completed with their numbered implementation slices; these controls are not yet claimed available.
