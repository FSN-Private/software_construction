# Lineage and Merge Validation Quickstart

1. Install the repository's pinned Node 24 and .NET 10 toolchains; start the existing PostgreSQL and MinIO integration services. Use the current feature 006 and 008 fixture commands in their feature validation records.
2. Run `dotnet test` for the lineage unit and integration project once T001 creates it. Verify branching, merge, hidden intermediate commits, 90-day recovery, unknown XMP, malformed XMP, source/target movement, duplicate confirmation, and gallery web working changes.
3. From `apps/web`, run `npm run typecheck`, `npm run test`, `npm run test:accessibility`, and `npm run test:browser`. Open a filtered gallery detail, enter lineage, compare two committed versions, and return with the same asset, branch, filters, sort, and page position.
4. Disable rendering and repeat comparison. Exact identities and textual differences must remain visible; each preview must report pending, failed, or unavailable without showing another version's image.
5. Prepare both a clean and a conflict merge. Inspect every affected image and result, require final confirmation, then move each input head before submitting a separate stale proposal. Only the fresh confirmed proposals may create commits.
6. Exercise keyboard and screen-reader navigation, Chrome/Edge/Firefox, three locale/time-zone settings, the 500-version p95 target, owner/cross-library denial, operation-ID telemetry, and mutation-disable/rollback behavior. Record evidence in `validation.md`.
