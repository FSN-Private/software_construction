# Research: Lineage and Merge Interface

## Decisions

1. **Graph authority**: Reuse feature 006's library-wide immutable DAG, image projection, refs, visibility, reflog, and recovery contracts. A screen-level graph is a labelled projection and must preserve exact parent IDs. Alternative: a separate image DAG. Rejected because it would invent history that does not exist.
2. **Metadata merge**: Reuse the seven gallery-editable metadata fields as the recognized v1 policy. Parse bounded XMP with DTD and external-entity processing disabled. Auto-combine only disjoint recognized changes; unknown darktable nodes, processing history, masks, malformed content, and overlaps stop for explicit resolution. Alternative: XML text merge. Rejected because valid XML does not imply a valid visual edit.
3. **Proposal lifetime**: Recompute proposals from exact source and target heads at confirmation instead of storing a mutable server-side draft. The confirm command contains the reviewed heads, policy version, chosen conflict identities, and idempotency key. Alternative: a persistent proposal table. Rejected because it adds cleanup and stale state without strengthening the feature 006 compare-and-swap publication.
4. **Preview provenance**: Request each image/version pair through feature 008. A pending, failed, or unavailable derivative remains a distinct state and never substitutes the current gallery image. Alternative: fall back to the current thumbnail. Rejected because it mislabels edit history.
5. **Web working state**: Gallery edits use feature 006's `__gallery_web__` client namespace. A browser branch switch must account for that namespace and device conflicts as well as its normal browser client. Alternative: call checkout on the browser client alone. Rejected because it can leave gallery edits attached to the old branch without an understandable path back.
6. **Generated content authority**: A new merged XMP digest needs verifiable asset/edit and input provenance before feature 006 can commit it. Add one provenance table for server-generated XMP and extend feature 006 validation to accept it only when the object is verified and its asset/edit matches. Alternative: spoof an upload session or mutate an edit identity's current XMP. Rejected because each misstates origin or silently changes existing identity. An abandoned object remains unreachable and follows normal content lifecycle rules.

## Integration evidence

- Feature 006 exposes `/graph`, `/compare`, `/refs`, `/working`, `/commits`, `/merges`, `/checkout`, `/rewrite`, `/reflog`, `/recovery`, `/visibility`, and `/restore` under its owner-scoped route. Merge publication requires a resolver-supplied staging set and publishes an ordered two-parent commit.
- Feature 008 exposes exact selected-version request, status, and derivative routes with provenance and explicit unavailable states.
- Feature 009 stores gallery web XMP as uncommitted working content and preserves browse filters and selected asset in the URL.

## Implementation guard

The existing content authority accepts original edit content, completed uploads, and gallery web content. The new provenance type is distinct and must be covered by integrity and reconstruction checks. No silent whole-side fallback is permitted.
