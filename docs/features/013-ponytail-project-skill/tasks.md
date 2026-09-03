# Tasks: Project-Scoped Ponytail Skills

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), and [quickstart.md](quickstart.md)

**Tests**: Unit tests are not applicable because the deliverable is immutable Markdown instruction content with no executable logic. Deterministic structural, front-matter, checksum, scope, and whitespace validation provides the required higher-level verification.

## Format: `[ID] [P?] [Story] Description`

## Phase 1: Source and Provenance Foundation

**Purpose**: Establish the exact reviewed upstream input before vendoring.

- [ ] T001 Confirm Ponytail tag `v4.8.4` resolves to commit `bc9ee949d5f439e8b9f3bb92c6d6d3d1e6ebd324`, review the six source skill files and MIT license, and record the evidence in `docs/features/013-ponytail-project-skill/third-party.md` (FR-004, NFR-003, NFR-009, NFR-015; AC-004; SC-003)

**Checkpoint**: Immutable source identity and license are documented before any third-party content is copied.

---

## Phase 2: User Story 1 — Use Ponytail in This Repository (Priority: P1) MVP

**Goal**: Make all six pinned Ponytail skills available through the repository's local skill convention without hooks or global state.

**Independent Test**: Run the structural and integrity command in `quickstart.md`; it must emit exactly six matching skill names and hashes.

- [ ] T002 [US1] Copy the exact upstream v4.8.4 `SKILL.md` files into `.agents/skills/ponytail/`, `.agents/skills/ponytail-audit/`, `.agents/skills/ponytail-debt/`, `.agents/skills/ponytail-gain/`, `.agents/skills/ponytail-help/`, and `.agents/skills/ponytail-review/` (FR-001, FR-002, FR-003, FR-006; AC-001, AC-003, AC-005; NFR-001, NFR-002, NFR-004, NFR-006, NFR-008, NFR-011, NFR-012, NFR-013, NFR-014; SC-002)
- [ ] T003 [US1] Add the complete included-file inventory, SHA-256 hashes, excluded-content statement, MIT notice, update procedure, and removal procedure to `docs/features/013-ponytail-project-skill/third-party.md` (FR-004, FR-005; AC-002, AC-004, AC-005; NFR-007, NFR-009, NFR-015; SC-003)
- [ ] T004 [US1] Run `docs/features/013-ponytail-project-skill/quickstart.md` validation, verify `git diff --check`, inspect the complete changed-file inventory for hooks/packages/global, application, or user-interface changes, and record results in `docs/features/013-ponytail-project-skill/tasks.md` (FR-005, FR-006; AC-001, AC-002, AC-003; NFR-001, NFR-003, NFR-005, NFR-006, NFR-007, NFR-010; SC-001, SC-002)

**Checkpoint**: The six skills are locally discoverable, byte-pinned, licensed, and validated without executable dependencies.

---

## Dependencies & Execution Order

- T001 establishes the trusted source and blocks T002.
- T002 blocks checksum finalization in T003.
- T003 blocks final validation in T004.
- No tasks are parallel because each consumes evidence produced by the previous task.

## Traceability Summary

| Specification item | Tasks |
|---|---|
| FR-001–FR-003, FR-006 | T002, T004 |
| FR-004 | T001, T003 |
| FR-005 | T003, T004 |
| AC-001–AC-003 | T002, T003, T004 |
| AC-004 | T001, T003 |
| AC-005 | T002, T003 |
| NFR-001–NFR-015 | T001–T004 as cited inline |
| SC-001–SC-002 | T002, T004 |
| SC-003 | T001, T003 |

## Implementation Strategy and Milestone

Complete T001–T004 sequentially, then create one coherent milestone commit containing the vendored skills, provenance, validation evidence, and current feature artifacts. No push is authorized by this request.
