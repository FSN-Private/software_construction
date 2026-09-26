# Data Model: Lineage and Merge Interface

| Entity | Fields and relationships | Invariants |
|---|---|---|
| Lineage page | Library, selected asset, branch/ref, exact head, offset, limit ≤100, commits, parent IDs, hidden/collapsed markers, ref/tag labels | Projection only; commit identities and parentage remain feature 006 authority. |
| Comparison | Asset/edit, base/target/source commit IDs, XMP content IDs, path/lifecycle differences, independent preview states and provenance | Every displayed rendition must match the selected immutable version. Missing state is explicit. |
| Merge proposal | Policy version, source and target refs and heads, base, affected asset/edit keys, proposed content identities, conflicts, preview status | Transient review artifact; invalid when either head or relevant working state moves. |
| Conflict | Asset/edit, field or opaque XMP category, base/target/source values or content IDs, selected result | No default selection for ambiguous content. Every conflict requires a reviewed choice. |
| History action | Action kind, expected ref/working version, idempotency key, confirmation, operation ID | Feature 006 performs mutation and audit; feature 010 cannot create independent graph truth. |
| Generated merge content | Owner, library, asset/edit, resulting XMP digest, exact base/target/source digests, policy version, operation ID | Durable provenance only for verified server-generated XMP; feature 006 validates identity and object before a commit. |

States: `prepared → reviewed → confirmed` or `prepared/reviewed → stale`; confirmation is one atomic delegated operation or no published commit. The browser may discard an unsent proposal without a server mutation. Hidden state changes presentation only; displaced commits retain the feature 006 recovery deadline.
