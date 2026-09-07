# Implementation Plan: Project-Scoped Ponytail Skills

**Branch**: `feature/013-ponytail-project-skill` | **Date**: 2026-09-03 | **Spec**: [spec.md](spec.md)

**Input**: Approved feature specification in `docs/features/013-ponytail-project-skill/spec.md`

## Summary

Vendor the six Markdown-only Ponytail v4.8.4 skills into the repository's existing `.agents/skills/` discovery path. Record immutable source provenance, per-file SHA-256 hashes, the MIT notice, validation, update, and removal instructions. Exclude all upstream hooks and packages so the integration remains project-scoped, offline, and free of executable dependencies.

## Technical Context

**Language/Version**: UTF-8 Markdown with YAML front matter; PowerShell 7-compatible validation commands

**Primary Dependencies**: No runtime dependency; source content pinned to Ponytail v4.8.4 commit `bc9ee949d5f439e8b9f3bb92c6d6d3d1e6ebd324`

**Storage**: Repository files only

**Testing**: PowerShell `Get-FileHash`, front-matter/name/path checks, `git diff --check`, and changed-file inventory

**Target Platform**: Codex hosts that discover repository-local `.agents/skills/<name>/SKILL.md`

**Project Type**: Repository agent-tooling configuration

**Performance Goals**: Zero executable startup work and zero network calls after checkout

**Constraints**: Project scope only; no Node.js; no global Codex state; preserve upstream skill text and MIT notice

**Scale/Scope**: Six skill files plus one provenance/license record and feature documentation

## Constitution Check

### Before design

- **I — Specification is the source of truth**: Pass. The approved specification, this plan, and `tasks.md` live under the matching feature directory.
- **II — Complete, testable delivery**: Pass. Every requirement and acceptance criterion maps to path, name, checksum, license, or inventory validation.
- **III — Non-functional quality and supply-chain care**: Pass. The source is authoritative and pinned; executable upstream content is explicitly excluded; provenance and update/removal strategy are documented.
- **IV — Feature branches and milestones**: Pass. Work occurs on `feature/013-ponytail-project-skill`; the branch began `0` behind and `0` ahead of `master`.
- **V — Independent pre-push review**: Pass for current scope. No push is requested; a separate alternate-model review remains mandatory before any later push.

### After design

All gates remain satisfied. No constitution exception or architecture decision record is needed because the change adds repository-local instruction content without changing application boundaries, data ownership, external interfaces, infrastructure, or security posture.

## Proposed Approach

1. Fetch the authoritative tag `v4.8.4` and verify it resolves to the approved immutable commit.
2. Copy only `skills/*/SKILL.md` for the six declared skills into matching `.agents/skills/<name>/` directories.
3. Add `docs/features/013-ponytail-project-skill/third-party.md` containing source identity, included paths, generated SHA-256 hashes, the MIT notice, excluded executable content, and update/removal instructions.
4. Validate the exact file set, YAML-front-matter names, hashes, absence of hooks/packages, Markdown whitespace, and clean repository scope.

## Affected Components

```text
.agents/skills/
├── ponytail/SKILL.md
├── ponytail-audit/SKILL.md
├── ponytail-debt/SKILL.md
├── ponytail-gain/SKILL.md
├── ponytail-help/SKILL.md
└── ponytail-review/SKILL.md

docs/features/013-ponytail-project-skill/
├── spec.md
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── tasks.md
├── third-party.md
└── checklists/requirements.md
```

## Interfaces and User Experience

No application API, UI, data schema, or service interface changes. A compatible Codex host discovers each skill from its conventional path in a new repository-rooted session. Contributors explicitly invoke focused skills or allow their descriptions to trigger normally. Existing `AGENTS.md` governance remains unchanged and higher priority.

## Data Model and Lifecycle

The only persisted entities are immutable vendored skill documents and their provenance record; see [data-model.md](data-model.md). Updating requires a new reviewed upstream revision and refreshed hashes. Removal deletes the six directories and provenance record. No runtime state, migration, or cleanup job exists.

## Validation and Error Behavior

Validation fails closed when the release commit differs, an expected file is absent, a declared name differs from its directory, a hash differs from the provenance record, or an unexpected executable/plugin file enters the change. The [quickstart](quickstart.md) supplies the runnable checks and expected results.

## Security and Privacy Design

Only Markdown instruction files are copied. Upstream JavaScript hooks, manifests, images, packages, install scripts, and network-capable code are excluded. No content reads secrets, invokes processes, writes user state, or sends data. Future updates repeat source and diff review rather than following a mutable branch.

## Compatibility, Migration, Rollout, and Rollback

- **Compatibility**: Existing repository-local skill convention; unsupported hosts ignore the files.
- **Migration**: None.
- **Rollout**: Commit the files; contributors start a new Codex thread so discovery refreshes.
- **Rollback**: Revert the milestone commit or delete the six skill directories and `third-party.md`; no residual project or global state remains.

## Alternatives Considered

- **Install the Codex marketplace plugin**: Rejected because installation is user-scoped and its automatic hooks require Node.js.
- **Merge Ponytail's root `AGENTS.md` into this repository**: Rejected because it would mix third-party instructions with governing project policy and omit focused skill commands.
- **Track upstream `main`**: Rejected because a mutable reference is not reproducible.
- **Vendor the entire repository**: Rejected because it imports unrelated adapters, hooks, packages, tests, and assets.

## Risks and Unresolved Decisions

- Instruction text is part of the development trust boundary. Pinning, exact copying, checksums, and review make changes explicit.
- Current-session discovery cannot prove fresh-session host behavior; structural validation and a documented restart step are the available local verification.
- No unresolved decisions remain.

## Milestone

One implementation milestone: vendor, document, validate, and commit the complete project-scoped Ponytail integration with all task statuses current.
