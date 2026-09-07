# Feature Specification: Project-Scoped Ponytail Skills

**Feature Branch**: `feature/013-ponytail-project-skill`

**Created**: 2026-09-03

**Status**: Approved — the maintainer authorized the project-scoped installation and the documented no-hook approach

**Owner**: Project maintainer

**Input**: Add Ponytail for this project without changing the user's global Codex installation.

## Problem Statement

Repository contributors using Codex should be able to invoke Ponytail's minimal-code guidance and focused review utilities from this repository. The full Codex plugin is user-scoped and its lifecycle hooks require Node.js, which is not available in the current environment, so it does not satisfy the requested project-only scope.

## Users, Goals, and Non-Goals

The users are contributors and coding agents working in this repository.

Goals:

- Make the upstream Ponytail skills discoverable from this repository.
- Preserve upstream behavior and licensing at an immutable reviewed release.
- Avoid executable hooks, global configuration, and runtime dependencies.

Non-goals:

- Installing or configuring Ponytail for other repositories.
- Automatically activating Ponytail through lifecycle hooks.
- Installing Node.js, an MCP server, npm packages, or marketplace state.
- Modifying Ponytail's upstream instructions.

## User Scenarios & Testing

### User Story 1 - Use Ponytail in this repository (Priority: P1)

A contributor starts a new Codex session in this repository and can select the vendored Ponytail skills for minimal implementation, review, audit, debt, gain, or help workflows.

**Why this priority**: Project-local discovery is the requested outcome.

**Independent Test**: Validate that all six expected skill directories contain parseable `SKILL.md` front matter and that their declared names are unique and match their directories.

**Acceptance Scenarios**:

1. **Given** a Codex session rooted in this repository, **When** project skills are discovered, **Then** `ponytail`, `ponytail-review`, `ponytail-audit`, `ponytail-debt`, `ponytail-gain`, and `ponytail-help` are available.
2. **Given** a new coding request that triggers Ponytail, **When** the skill is loaded, **Then** the upstream v4.8.4 instructions are used without requiring Node.js or a global plugin installation.
3. **Given** the integration is removed, **When** the six vendored skill directories and attribution record are deleted, **Then** no Ponytail runtime state remains in the project.

### Edge Cases

- A host that does not support repository-local Codex skills ignores the files without affecting application behavior.
- A future upstream release does not alter the pinned local copy until a separately reviewed update is committed.
- Missing or altered skill files fail repository validation rather than silently presenting a partial skill set.
- Existing repository governance continues to override conflicting third-party guidance.

## Requirements

### Functional Requirements

- **FR-001**: The repository MUST provide all six Ponytail v4.8.4 skills under `.agents/skills/` using their upstream skill names and content.
- **FR-002**: The integration MUST be scoped to this repository and MUST NOT modify user-global Codex configuration or plugin marketplaces.
- **FR-003**: The integration MUST NOT include or execute Ponytail lifecycle hooks, npm packages, or other runtime code.
- **FR-004**: The repository MUST record the authoritative upstream repository, release `v4.8.4`, commit `bc9ee949d5f439e8b9f3bb92c6d6d3d1e6ebd324`, copied paths, MIT license, review date, update method, and removal method.
- **FR-005**: A deterministic validation MUST fail if an expected skill is missing, misnamed, lacks readable front matter, or differs from the recorded upstream checksums.
- **FR-006**: Existing repository instructions and workflow gates MUST remain unchanged and authoritative.

### Acceptance Criteria

- **AC-001**: Validation finds exactly the six expected Ponytail skill names in their matching project-local directories.
- **AC-002**: Every vendored `SKILL.md` SHA-256 checksum matches the recorded provenance for upstream v4.8.4.
- **AC-003**: The change contains no executable hook, package manifest, lockfile, global configuration, or application-code modification.
- **AC-004**: The provenance record contains the complete MIT license notice and immutable upstream commit.
- **AC-005**: Removing the six skill directories and provenance record fully rolls back the integration without migration or retained state.

## Non-Functional Requirements

- **NFR-001 Performance**: Skill discovery adds no executable startup work and no network calls. Verification: file and hook scan.
- **NFR-002 Availability**: The skills remain usable offline after checkout. Verification: local-only validation.
- **NFR-003 Security**: No third-party executable code, install script, or lifecycle hook enters the repository trust boundary. Verification: changed-file and content scan.
- **NFR-004 Privacy**: The integration reads or transmits no repository or user data. Verification: changed-file inspection.
- **NFR-005 Accessibility**: Not applicable — this change adds agent instruction files and no user interface.
- **NFR-006 Compatibility**: The files use the repository's existing `.agents/skills/<name>/SKILL.md` convention and UTF-8 Markdown. Verification: path and encoding checks.
- **NFR-007 Reliability**: Validation detects any missing or altered vendored skill with 100% coverage of the six files. Verification: checksum comparison.
- **NFR-008 Observability**: Not applicable — no runtime process or service is introduced.
- **NFR-009 Maintainability**: Updates are reviewable by replacing from an immutable upstream release and regenerating recorded checksums. Verification: provenance review.
- **NFR-010 Testability**: One deterministic PowerShell validation command checks names, paths, and hashes without additional dependencies. Verification: run the command from a clean checkout.
- **NFR-011 Localization**: Not applicable — upstream agent instructions are intentionally preserved in their source language.
- **NFR-012 Deployment**: Not applicable — repository checkout is the distribution mechanism.
- **NFR-013 Operations**: Rollback requires only deletion of the vendored files and attribution record; no state cleanup is required. Verification: file inventory.
- **NFR-014 Cost**: The integration introduces no paid service, package installation, or runtime compute. Verification: dependency review.
- **NFR-015 Compliance**: The upstream MIT copyright and permission notice MUST be retained with the vendored copy. Verification: license-text comparison.

## Success Criteria

- **SC-001**: All six expected Ponytail skills pass path, name, and checksum validation in one local command.
- **SC-002**: Zero executable hooks, package dependencies, application files, or global settings are added.
- **SC-003**: A contributor can identify the exact upstream release, commit, license, update procedure, and rollback procedure from one repository document.

## Constraints, Assumptions, and Dependencies

Constraints:

- Repository governance and Spec Kit requirements remain authoritative.
- The integration must work without Node.js and without network access after checkout.

Assumptions:

- Codex discovers repository-local skills under `.agents/skills/` on a new session.
- The user's approval applies to the project-scoped, v4.8.4, no-hook approach proposed immediately before approval.

Dependencies:

- Authoritative source: `https://github.com/DietrichGebert/ponytail`.
- Pinned release: `v4.8.4` at commit `bc9ee949d5f439e8b9f3bb92c6d6d3d1e6ebd324`.
- License: MIT.

## Risks and Open Questions

- Upstream skill instructions can influence future agent output; exact vendoring and checksums make changes explicit and reviewable.
- Project-local skill discovery may require starting a new Codex thread after checkout.
- No material questions remain for the approved scope.
