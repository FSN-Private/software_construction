# Implementation Plan: Gallery Metadata Search

**Branch**: `feature/009-gallery-metadata-search` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)  
**Status**: Proposed — pending approval

## Summary

Provide a private React gallery backed by PostgreSQL normalized metadata facets, deterministic keyset pagination, feature-008 rendition/provenance state, and feature-006 branch-scoped XMP working changes. The gallery reads source authority from prior features; it never edits originals or presents unsupported darktable controls.

## Design

Metadata ingestion maps embedded/XMP values into versioned normalized facets with source, clear-vs-missing state, and sort timestamp. PostgreSQL B-tree/GIN indexes and cursor tokens containing sort tuple plus asset ID give stable 100k-asset pagination. A browse context stores filters, sort, cursor, and geolocation visibility so detail return preserves results. Cards show rendition complete/pending/failed state without substitution; details expose source precedence, working indicator, and exact provenance.

Allowed edits build a minimal namespace-aware XMP patch for rating, rejection, color label, tags, title, description, and geolocation, preserve unrelated XMP content, and submit expected-version/idempotent branch working change to feature 006. Stale state yields refresh/retry/conflict. Web changes are visible to feature-007 devices on that branch. Geolocation suppression is an owner presentation preference: hidden cards/results omit it but underlying XMP remains intact. Portal lifecycle actions delegate to feature 005.

Accessibility covers keyboard filter/sort/pagination/edit validation and textual preview status. Owner authorization, redacted telemetry, indexed-refresh progress, expand/migrate/contract schema changes, and no-original retrieval during normal browsing are mandatory. **PASS**: current branch is 44 ahead/0 behind `master`; no external mutation is authorized.
