# Feature Specification: Backup and Disaster Recovery

**Feature Branch**: `feature/011-backup-disaster-recovery`  
**Created**: 2026-09-03  
**Status**: Draft — awaiting product-owner approval  
**Parent program**: [003 Darktable Version Control](../003-darktable-version-control/spec.md)  
**Related decision**: [ADR-0003](../../architecture/decisions/0003-cross-region-library-recovery.md)

## Problem and Scope

The library is irreplaceable: originals, XMP history, metadata, and recovery evidence must survive accidental deletion, corruption, and loss of the primary AWS region. The owner needs transparent backup health and a rehearsed recovery path, including economical cold storage.

This feature defines backup, retention, recovery, and disaster-recovery outcomes for all library data. It uses an independent AWS recovery account and a second region, with cold storage for long-term recovery copies. It excludes routine gallery use, user-facing version editing, and any immediate production deployment or data migration.

## User Scenarios & Testing

### User Story 1 - Verify protection (Priority: P1)

As a photographer, I can see whether every library data class has a recent protected recovery copy, so I can act before an emergency.

**Independent Test**: Create representative originals, XMP versions, metadata, and audit records; complete a backup cycle; and verify their protection status and age.

### User Story 2 - Recover accidental loss (Priority: P1)

As a photographer, I can restore an accidentally deleted or displaced object within its retention window without overwriting the current library silently.

**Independent Test**: Delete and displace fixtures, recover them into a reviewable location, validate identity and history, then explicitly promote them.

### User Story 3 - Recover from regional loss (Priority: P1)

As the owner, I can restore the private library in the recovery region and continue safely after a declared primary-region loss.

**Independent Test**: Run a documented recovery rehearsal using only recovery-account, second-region materials and verify RPO/RTO objectives.

### Edge Cases

- Backup is late, incomplete, corrupt, inaccessible, or produced from an already corrupted source.
- The primary account, region, or credentials are unavailable during recovery.
- Cold-storage retrieval is pending while an urgent recovery is requested.
- A restore collides with newer live data, or an integrity check fails.
- A retention/purge action would remove the last valid recoverable copy.

## Requirements

### Functional Requirements

- **FR-001**: The system MUST classify originals, XMP objects and version graph, library metadata, previews as disposable derivatives, audit records, configuration, and recovery records by backup requirement.
- **FR-002**: The system MUST create recovery copies in a separate AWS account and a second AWS region from the primary service.
- **FR-003**: The system MUST protect originals, XMP objects, version graph, metadata, audit records, configuration, and recovery records against loss of the primary region.
- **FR-004**: The system MUST place long-retention recovery copies in owner-approved cold storage while retaining enough accessible recovery material to meet the recovery objective.
- **FR-005**: The system MUST verify backup completeness and integrity against immutable source identities and record the result.
- **FR-006**: The system MUST surface backup freshness, completeness, integrity, last successful verification, and exceptions to the owner.
- **FR-007**: The system MUST target a recovery point objective of no more than one hour of acknowledged library changes.
- **FR-008**: The system MUST target recovery of the private library's core originals, XMP history, metadata, and owner access within 24 hours of declared primary-region loss.
- **FR-009**: The system MUST provide an owner-approved restore workflow that restores into an isolated review state before promotion.
- **FR-010**: The system MUST validate restored source hashes, XMP/version relationships, metadata consistency, and audit continuity before promotion.
- **FR-011**: The system MUST retain displaced commits and deletion-recovery evidence for at least 90 days, as required by the parent program.
- **FR-012**: The system MUST prevent automated retention actions from removing the final verified recovery copy of a protected data class.
- **FR-013**: The system MUST record an auditable recovery event for backup failures, restore requests, validation, promotion, drills, and declared disasters.
- **FR-014**: The system MUST maintain a tested, owner-readable regional-disaster runbook with decision owner, prerequisites, abort thresholds, validation, and rollback limits.
- **FR-015**: The system MUST rehearse a full second-region recovery at least quarterly and record achieved RPO/RTO and corrective actions.
- **FR-016**: The system MUST use least-privilege, independently controlled recovery access and MUST not require primary-region credentials for a regional recovery.

### Key Entities

- **Recovery copy**: Protected copy of a classified library data set in the recovery account and region.
- **Recovery point**: A verified, time-stamped set of recoverable library state.
- **Restore session**: Isolated, auditable recovery work prior to promotion.
- **Recovery drill**: Planned rehearsal with measured objectives and follow-up actions.
- **Retention hold**: Protection preventing premature deletion of required recovery evidence.

## Acceptance Criteria

- **AC-001**: Every protected fixture data class has a verified independent recovery copy after a successful cycle.
- **AC-002**: Backup status identifies an overdue, incomplete, or integrity-failed class within one hour of detection.
- **AC-003**: A simulated primary-region loss can recover the core library using only recovery-account and second-region materials.
- **AC-004**: The simulation achieves no more than one hour of acknowledged data loss and restores core access within 24 hours.
- **AC-005**: A restore first enters isolated review and cannot overwrite live data without explicit promotion.
- **AC-006**: A corrupted or mismatched restored object fails validation and cannot be promoted.
- **AC-007**: A retention test cannot remove the final verified copy of a protected class.
- **AC-008**: A quarterly drill creates an audit record containing achieved objectives and any corrective actions.

## Non-Functional Requirements

- **NFR-001 Availability**: Recovery materials MUST remain independently accessible during loss of the primary AWS account or region.
- **NFR-002 Reliability**: Recovery success MUST be based on verified integrity, not only backup-job completion.
- **NFR-003 Performance**: Recovery status for a normal library MUST load within 5 seconds for 95% of requests under nominal conditions.
- **NFR-004 Security**: Recovery accounts, data, and credentials MUST use least privilege and separation from primary-service administration.
- **NFR-005 Privacy**: Recovery copies MUST preserve the library's private access boundary and encrypt protected photo and metadata content.
- **NFR-006 Compliance**: Retention, deletion, and recovery evidence MUST honor the 90-day parent-program recovery policy.
- **NFR-007 Observability**: Operators MUST measure backup age, coverage, integrity, drill RPO/RTO, restore duration, and retention exceptions.
- **NFR-008 Maintainability**: The data-classification and runbook MUST be reviewed after every drill and material architecture change.
- **NFR-009 Testability**: Automated checks and scheduled drills MUST prove backup coverage, integrity validation, restore isolation, and retention holds.
- **NFR-010 Operations**: Each production recovery action MUST have an identified decision owner, observable abort thresholds, and recorded evidence.
- **NFR-011 Cost**: Cold-storage use and retrieval expectations MUST be visible to the owner before long-term retention policy changes.
- **NFR-012 Recovery**: The RPO and RTO in FR-007 and FR-008 are binding release criteria, not aspirational targets.
- **NFR-013 Accessibility**: Recovery-health, exception, and runbook status information MUST be available in accessible text form.
- **NFR-014 Localization**: Owner-facing recovery statuses and instructions MUST be localizable; timestamps and immutable identifiers remain unambiguous.
- **NFR-015 Compatibility**: Recovery procedures MUST work without dependence on the primary-region application runtime.
- **NFR-016 Auditability**: Backup, restore, drill, and retention events MUST be tamper-evident and retained with their associated recovery evidence.

## Assumptions and Dependencies

- The parent program established the single-owner private library, 90-day recovery window, multi-AZ primary operation, and second-region recovery copy.
- Features 004–010 identify the data and contracts to be protected; this feature supplies recovery guarantees across them.
- No production deployment, account creation, migration, or restore is authorized by this specification.

## Success Criteria

- **SC-001**: Every quarterly drill demonstrates a verified recovery point no more than one hour behind the declared loss point.
- **SC-002**: Every quarterly drill restores core private-library access within 24 hours using recovery-account materials only.
- **SC-003**: 100% of protected fixture classes pass coverage and integrity validation in a successful recovery cycle.
- **SC-004**: No retention test removes the final verified recovery copy of a protected data class.
