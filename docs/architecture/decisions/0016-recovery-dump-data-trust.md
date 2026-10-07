# ADR-0016: Recovery dump data must not supply executable authority

- Date: 2026-10-07
- Status: Accepted for local implementation under approved feature requirements; external deployment remains gated
- Decision owner: Repository owner
- Feature: [011](../../features/011-backup-disaster-recovery/spec.md), T037

The native runner currently restores a primary-produced custom archive as the recovery database administrator before validating its migration ledger and graph. Matching migration tracking rows do not authenticate function bodies, triggers, constraints or archive COPY commands. A source compromise must not acquire recovery database authority through those bytes.

Build the isolated schema using only the independently pinned release migration files. Compare captured schema against this authority before loading data. Extract archive data as a stream without executing its SQL; permit COPY only into exact known tables and columns, validate non-data directives, and reject arbitrary commands. Temporarily suppress only trusted import-time triggers/foreign keys inside the isolated import transaction, then restore their trusted definitions and run the existing complete schema, graph, audit, metadata and closure checks. Never run producer-supplied functions or triggers.

Continuing raw privileged restore preserves arbitrary source schema but exposes the independent database to source SQL. Using an unprivileged raw restore limits cluster privileges but still executes untrusted code and can expose data through database logs. Pinned schema plus validated data preserves approved schema/data while refusing untracked executable changes; unsupported archive constructs fail closed rather than receiving administrator permission.

This adds stream validation and an authoritative schema comparison. It needs native PostgreSQL tests, precise resource limits, compatibility coverage and attributable failure evidence. Existing captures remain custom PostgreSQL18 dumps; independent release/migration pins, owner identity, immutable graph and audit data remain binding. Abort by discarding the isolated database. There is no rollback to privileged producer-code execution and no live migration or deployment authorization.
