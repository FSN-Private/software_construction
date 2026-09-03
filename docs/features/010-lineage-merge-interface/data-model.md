# Lineage and Merge Data Model

`LineageView` is a paged projection of immutable commits; `Comparison` binds exact versions/common ancestor/diff/provenance; `MergeProposal` binds heads, policy, inputs, selected resolutions, and confirmation state; `Conflict` holds base/ours/theirs/chosen identity; `VisibilityPreference` is presentation-only. All mutable proposals become invalid on source/target head change.
