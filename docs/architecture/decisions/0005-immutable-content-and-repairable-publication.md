# ADR-0005: Immutable Content and Repairable Publication

**Date**: 2026-09-03
**Status**: Proposed
**Owners**: Project maintainer
**Feature**: [005 Image Ingest and Durable Storage](../../features/005-image-ingest-storage/spec.md)

## Context

Image/XMP bytes belong in object storage while asset identity, paths, lifecycle, and associations need transactions. Acknowledging a transfer before both systems agree risks an unrecoverable or unreachable asset.

## Decision

Use SHA-256/length-addressed immutable S3 objects with PostgreSQL asset/edit/path/lifecycle metadata. Create a database publication intent before object promotion, then atomically publish related metadata only after verification; reconciliation retries or safely cleans pending work. Keep unverified or ambiguous inputs private/non-authoritative. Deduplicate payloads internally without merging logical identities. Use 90-day hold-aware soft deletion and canonical checksummed reconstruction manifests.

## Alternatives and consequences

Two-phase commit across S3/PostgreSQL is unavailable; best-effort dual write was rejected because it can acknowledge orphaned content. Database blobs and path identity were rejected for lifecycle and move semantics. The outbox adds reconciliation and monitoring work, but failure is visible and repairable. This decision introduces no authority to create buckets or manipulate user data; feature 011 owns final purge and recovery copies.
