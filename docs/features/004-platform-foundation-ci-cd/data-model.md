# Foundation Data Model

Foundation records are versioned, audit-safe, and exclude image/XMP payloads, geolocation, credentials, and tokens.

| Entity | Immutable identity / fields | Lifecycle |
|---|---|---|
| Environment | ID, account/region, role ARN, configuration digest, protection state | planned → verified → active → retired |
| Validation check | revision SHA, stable name, run ID, evidence URI, result | queued → running → pass/fail/cancelled/skipped/stale |
| Release set | release ID, source SHA, manifest digest, artifact digests, evidence references | built → verified → nonprod healthy → production approved → production healthy / superseded |
| Deployment | deployment ID, release/environment IDs, actor/approval, health evidence | pending → deploying → healthy / failed / rolled back |
| Rollback record | source/target releases, compatibility decision, actor, reason, evidence | requested → validated → complete / refused |
| Owner identity | opaque Cognito subject, provision state, audit reference | provisioned → active → disabled |
| Operation signal | operation ID, trace links, redaction class, alert/runbook reference | emitted → retained → expired |
| Dependency record | exact identity, source, license, advisory state, review evidence | proposed → accepted / blocked / superseded |

A revision has many checks; a release derives from one revision and has many artifacts/deployments; deployments belong to an environment; a rollback references two releases; signals correlate requests, deployments, and alerts.
