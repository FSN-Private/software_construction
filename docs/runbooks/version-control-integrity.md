# Version-control integrity and projection rebuild

Owner: platform operator. Scope: Feature 006 immutable graph and derived projections.

Alert when `platform.version_control.integrity_failures` is non-zero for one library or when the scheduled verification has not succeeded for 15 minutes. Keep reads of previously verified commits available, but stop ref advances for quarantined refs. Never repair commit bytes, identities, or parent rows in place.

1. Record the operation ID, library ID, affected ref, and safe integrity codes; do not copy XMP, paths, filenames, or descriptive metadata into tickets or logs.
2. Run `POST /v1/libraries/{library}/version-control/integrity/verify`. A healthy result reports the number of checked commits. `graph_corrupt` leaves refs quarantined.
3. Restore missing immutable content from Feature 005 recovery or restore the exact database rows from verified reconstruction evidence. If a manifest digest or parent relation differs, restore the original row; never recalculate a new ID for old history.
4. Repeat verification. Only a healthy result removes quarantine. Then run `POST .../projections/rebuild`; compare row counts and sample whole-library/image projections to immutable graph evidence.
5. Abort if any digest, parent, library scope, or content identity still differs. Escalate to the owner; a ref must not advance during unresolved corruption.

Rollback: projection rebuild is replaceable derived state, so rerun it from the last verified graph. Immutable commits have no destructive rollback path.
