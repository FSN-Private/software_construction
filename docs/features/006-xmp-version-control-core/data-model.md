# Version-Control Core Data Model

| Entity | Essential fields / invariant |
|---|---|
| Working change | owner/client/branch, asset/edit ID, base/current XMP IDs, state, version; mutable and auditable |
| Staging set | ordered selected changes, expected head, owner/client, version/status |
| Commit/entry | immutable manifest digest, ordered parents/entries, author/times/message/policy; entries reference exact feature-005 content |
| Branch/tag | normalized name, library, exact head, protection/version; tags immutable except confirmed replace |
| Reflog/recovery ref | old/new head, actor/reason/time, expiry/reachability; holds required content |
| Visibility | owner/library/commit presentation state only; cannot alter parentage/reachability |
| Materialized state/projection | derived head/image view plus source manifest version; rebuildable, never authority |
| Divergence set | base/ours/theirs identities and affected entries; unresolved means no ref advance |

Commits form an acyclic library DAG; refs point to commits; every reachable/recovery/hidden-retained commit obtains required feature-005 holds.
