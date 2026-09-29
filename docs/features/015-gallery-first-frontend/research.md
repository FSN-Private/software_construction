# Existing Frontend Research

- Decision: existing React/Vite and native dialog. Rationale: browser/dependencies suffice; adding a UI framework is unnecessary.
- Decision: scoped path/title/description search and exact rating. GalleryBrowseService supports discrete AND-composed facets, not generic OR search/minimum rating. New backend semantics are outside scope.
- Decision: reuse history/compare and VersionPreview. Compare has exact identities/provenance, not full historical fields; explicit unavailable disclosure prevents current metadata misrepresentation.
- Decision: preserve mounted gallery during secondary views and use existing branch safeguards. No new router/state library.
- All choices resolved by local source inspection; no delegated research under repository authorization, no extension hooks.
