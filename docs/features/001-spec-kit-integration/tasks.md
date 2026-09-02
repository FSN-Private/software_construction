# Tasks - Spec Kit integration

**Status:** Complete; pre-push review remains required only if a push is requested
**Source:** [spec.md](spec.md) and [plan.md](plan.md)

## Phase 1 - Documentation and decision

- [x] T001 Create the approved Spec Kit specification at `docs/features/001-spec-kit-integration/spec.md` (`FR-1` through `FR-8`, `NFR-1` through `NFR-8`).
- [x] T002 Create the technical implementation plan at `docs/features/001-spec-kit-integration/plan.md` (`FR-1` through `FR-8`).
- [x] T003 Create ADR `docs/architecture/decisions/0001-adopt-spec-kit.md` (`FR-5`, `FR-7`, `NFR-1` through `NFR-5`).
- [x] T004 Commit the approved design artifacts.
  - Validation: `git diff --check` and review staged paths.
  - Milestone: Integration design documented.

## Phase 2 - Official tooling and initialization

- [x] T005 Install official Astral `uv` and verify `uv --version` (`FR-1`, `NFR-1`, `NFR-2`).
- [x] T006 Install `specify-cli` from `github/spec-kit@v1.0.3`; verify `specify version` and `specify check` (`FR-1`, `AC-1`, `NFR-1` through `NFR-4`).
- [x] T007 Initialize the current repository with `--here --force --integration codex --script ps` after recording a clean-state baseline (`FR-2`, `FR-3`, `AC-2`, `AC-3`).
- [x] T008 Inspect generated files, reconcile `AGENTS.md`, and preserve existing skills (`FR-3`, `FR-6`, `AC-3`, `AC-4`, `NFR-5`).
  - Validation: compare skill directory names before and after initialization; `git diff --check`.
  - Milestone: Spec Kit scaffolded for Codex.

## Phase 3 - Governance and validation

- [x] T009 Write `.specify/memory/constitution.md` with repository requirements and Spec Kit artifact mapping (`FR-4` through `FR-6`, `AC-2`, `AC-4`).
- [x] T010 Update repository documentation and skill routing for `docs/features/<NNN>-<slug>/` and `SPECIFY_FEATURE_DIRECTORY` (`FR-5`, `FR-6`, `AC-4`, `AC-5`).
- [x] T011 Validate the initialized structure, command metadata, custom-skill preservation, and absence of `specs/` (`FR-8`, `AC-2` through `AC-5`).
- [x] T012 Record unit-test applicability and commit the completed integration (`FR-8`, `AC-7`).
  - Validation: all commands in `plan.md`; no project-owned executable logic was added, so unit tests are not applicable.
  - Milestone: Spec Kit integration complete.

## Phase 4 - Pre-push gate

- [ ] T013 Run the alternate-model adversarial pre-push review and record its result if a push is requested.

## Traceability

| Requirement | Tasks | Validation |
| --- | --- | --- |
| FR-1 / NFR-1 through NFR-4 | T005-T006 | CLI versions and provenance review |
| FR-2 / FR-3 | T007-T008 | Generated tree and preserved-skill comparison |
| FR-4 through FR-6 | T009-T010 | Constitution and routing review |
| FR-7 | T003 | ADR review |
| FR-8 / NFR-5 through NFR-8 | T011-T012 | Structural checks and documented test applicability |
| AC-1 through AC-7 | T004-T013 | Per-task validation and milestone commits |
