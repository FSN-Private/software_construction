# Lineage and Merge Interface Contract v1

All routes below `/v1/libraries/{library}/lineage` require the authenticated library owner, return `Cache-Control: private, no-store`, and use safe `code` plus `operationId` errors. Mutations require `Idempotency-Key` and exact expected identities. The existing feature 006 endpoints remain the authority for commit/ref/rewrite/visibility/checkout/recovery operations.

| Operation | Required response and behavior |
|---|---|
| `GET /images/{asset}/history?branch=&offset=&limit=&showHidden=` | At most 100 exact commits per page, parent IDs, ref/tag labels, current selection, collapsed-hidden indicator, and next-page context; a view label says it is image-filtered. |
| `GET /images/{asset}/compare?target=&source=` | Exact target/source/base commit and XMP identities, recognized-field differences, opaque differences, and two independent preview/provenance states. No-current-preview fallback. |
| `POST /merges/prepare` | Source/target ref and head, base or explicit absence, policy version, complete affected asset/edit list, proposed XMP identities, conflicts, preview states, and a canonical proposal digest. No mutation. |
| `POST /merges/confirm` | Reviewed proposal digest, exact expected heads, complete conflict choices, and confirmation. Re-evaluate both heads and content, then pass prepared resolver entries to feature 006 publication. Its transaction checks both refs and returns immutable commit/operation ID or no commit. |

`stale_head`, `stale_working_state`, `unresolved_divergence`, `protected_ref`, `dirty_checkout`, `content_not_authoritative`, `preview_unavailable`, and `recovery_expired` never include private XMP, location, path, or commit-message content. Confirm replays with the same idempotency key and payload return the original result; a changed payload under the same key is rejected. The owner must refresh and review a changed proposal before a new confirmation.

Server-generated combined XMP carries owner/library/asset/edit and exact base/target/source digest provenance. The generated result is accepted by feature 006 only after object verification; the browser cannot submit arbitrary XMP through this contract.

The web's branch, tag, commit, hide/reveal, restore/revert, recovery, rewrite, and checkout controls delegate to feature 006's [version-control contract](../../006-xmp-version-control-core/contracts/version-control-v1.md). The gallery entry retains its URL query and displays uncommitted web changes separately from committed history.
