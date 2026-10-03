# Platform conventions v1

These JSON fixtures are the canonical, versioned conventions that feature 004 publishes for child features. They are deliberately data-only and contain no live account, user, image, XMP, location, token, or credential value.

- `error.problem.json` defines the safe `application/problem+json` shape.
- `operation-id.json` and `idempotency.json` define request-correlation inputs.
- `audit-event.json` and `telemetry-redaction.json` define audit-safe and redaction behavior.
- `health.json`, `configuration.json`, `migration.json`, and `promotion.json` define platform lifecycle evidence.

Compatibility rules: additions are backward compatible; removing or changing the meaning of an existing field requires a new contract version and a migration plan. Contract tests validate the required v1 shapes with `npm run test:contract`.

## Versioned export contracts

Feature 012 contracts live in `Exports/`: schema-1 immutable pins and snapshots, mandatory per-item manifests, original-only provenance, versioned JPEG metadata transforms, Windows-safe collision-free output names, quota validation and scoped purge invalidation. Presets always include a manifest; original bytes retain embedded metadata and require acknowledgment. These records do not own retention holds.
