# Gallery Contract v1

`GET /gallery` accepts versioned filters/sort/cursor and returns stable result/count/facet state; `GET /assets/{id}` returns source metadata, working state, rendition/provenance; allowed metadata `PATCH` requires branch/base/idempotency; lifecycle operations delegate to feature 005. Errors include `stale_working_state`, `unsupported_edit`, `hidden_geolocation`, and rendition state; no sensitive values are logged.
