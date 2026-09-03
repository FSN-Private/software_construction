# Gallery Data Model

`GalleryAsset` references feature-005 identity; `FacetValue` stores normalized value, source, cleared/missing state, and schema version; `BrowseContext` stores filters/sort/cursor/geolocation preference; `WebEdit` stores allowed patch/base/branch/idempotency/outcome. Facets refresh atomically from a source version; mixed records are never published.
