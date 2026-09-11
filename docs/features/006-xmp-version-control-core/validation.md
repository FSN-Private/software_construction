# Feature 006 validation and migration rehearsal

## Local checks

Run `npm run test:version-control`, `npm run test:backend`, `npm run format`, and `git diff --check`. With the pinned PostgreSQL integration service running, set `VERSION_CONTROL_INTEGRATION=1` before the focused test to exercise the real schema, 100-entry atomic commits, injected transaction failures, 100 idempotent replays, and ten-way expected-head contention.

## Schema and manifest compatibility

`006_xmp_version_control.sql` is expand-only: it adds `vc_*` tables and indexes and does not alter Feature 005 tables. The checksum-verified migrator runs the file in one transaction and rejects an applied migration whose bytes changed. Reapplying the migrator is a no-op. Manifest v1 has a locked canonical byte/digest fixture; an unknown contract version fails explicitly instead of being reinterpreted.

Rehearsal sequence:

1. Apply 005 migrations, seed an authoritative asset/XMP identity, and prove the Feature 005 reader still returns it.
2. Begin a transaction, apply 006, inject a failure, and roll back; prove no `vc_*` table is visible and the seeded identity is unchanged.
3. Apply 006 and record its checksum. Create/read a v1 commit, rebuild projections, and compare identities.
4. Apply the migrator again; prove one 006 migration record and unchanged commit/ref counts.
5. Run the previous API build against the expanded schema; it ignores `vc_*`. Run the new API against an empty graph; it returns an empty/ref-not-found result without changing Feature 005 state.

## Rollback and forward recovery

Before Feature 006 writes production commits, application rollback is safe because the prior release ignores the additive tables. After writes begin, roll back application binaries only while retaining every `vc_*` table and lifecycle hold; dropping them would make acknowledged history unavailable and is forbidden. Forward recovery redeploys a reader supporting the stored manifest version, verifies graph integrity, and rebuilds derived projections. A future contract migration must expand readers first, write the new version only after mixed-version validation, and remove old readers only after a verified rebuild and rollback-window closure.

No production migration or deployment was performed by this feature implementation request.

## Completion evidence — 2026-09-11

- Focused PostgreSQL integration and capacity checks passed with a 755 ms 100-entry commit, 2.3 ms per-asset p95, a 9 ms 10,000-node first page, and a 0.24 s one-million-entry/100,000-asset model rebuild.
- Adversarial remediation T024–T026 passed PostgreSQL 18 on 2026-09-11: a 919 ms 100-entry commit, 2.0 ms per-asset p95, 9 ms 10,000-node first page, and 0.24 s one-million-entry model. The suite additionally proved canonical/normalized evidence checks, owner/asset/edit provenance rejection, pre-advance quarantine, descendant-ref retention, and deterministic rebuild/publication fencing.
- Adversarial remediation T027–T030 passed PostgreSQL 18 on 2026-09-11: a 897 ms 100-entry commit, 1.9 ms per-asset p95, 11 ms 10,000-node page, and 0.23 s one-million-entry model. Durable ref/restore replay, stash round-trip/client fencing, stale restore rejection, non-tip revert isolation, slash refs, reflog/direct lookup, rename-in-use rejection, and omitted-conflict merge rejection were exercised.
- Adversarial remediation T031 passed locally on 2026-09-11. The exact required-CI PostgreSQL/capacity command reported an 867 ms 100-entry commit, 1.9 ms regular asset-state p95, persisted 10,000-node lineage p95 235 ms, persisted indexed asset-state p95 2.2 ms, and a 0.24 s one-million-entry/100,000-asset model. Infrastructure synthesis/assertions, reliability contracts, the full root/web/infrastructure online dependency audit, secret scan, dependency tree, and license checks passed; only the existing CDK deprecation and unconfigured-feature-flag warnings remain. A remote CI run is not claimed before push.
- `npm test`, API correlation, .NET formatting/build, web typecheck/build/tests/accessibility, contract, CI-contract, reliability, infrastructure synthesis/assertions, secret scan, dependency tree, license, and online advisory checks passed.
- Migration rollback/reapply, immutable-row rejection, idempotent replay, ten-way CAS, staged/base drift, projection freshness/rebuild, canonical corruption quarantine, exact day-90 recovery, lifecycle holds, and two-parent merge rollback were exercised against PostgreSQL 18.
- `git diff --check` passed. The repository-wide web Prettier command remains non-actionable on this Windows checkout because it reports CRLF style differences in thirteen untouched baseline web files; no unrelated formatting rewrite was included.
