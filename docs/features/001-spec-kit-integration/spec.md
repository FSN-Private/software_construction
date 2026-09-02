# Spec Kit integration specification

**Status:** Approved
**Feature:** 001-spec-kit-integration
**Owner:** Project maintainer
**Decision date:** 2026-09-02

## Problem and goals

The repository has a documented custom feature-delivery workflow but no executable Spec Kit workflow. Introduce GitHub Spec Kit so Codex can create, plan, task, analyze, implement, and converge feature work consistently while preserving the repository's existing requirements for documentation, tests, milestone commits, and independent pre-push review.

Goals:

- install the official, pinned Specify CLI for repeatable local use;
- initialize the existing repository for Codex using PowerShell scripts;
- keep all feature specifications under `docs/features/`;
- make Spec Kit's `spec.md`, `plan.md`, and `tasks.md` the canonical feature artifacts;
- encode the repository's existing governance in the Spec Kit constitution;
- preserve all existing custom skills and agent instructions.

Non-goals:

- install GitHub Copilot, another agent integration, Spec Kit's optional Git extension, presets, or community extensions;
- replace the repository's existing skills with Spec Kit-managed skills;
- create CI workflows, application features, or a GitHub pull request.

## Functional requirements

- **FR-1:** Install `uv` from Astral's official distribution and install `specify-cli` from `github/spec-kit` pinned to `v1.0.3`.
- **FR-2:** Initialize this non-empty repository with the `codex` integration and PowerShell scripts using the official Specify CLI. Generated Codex commands must be discoverable as `$speckit-*` skills in `.agents/skills/`.
- **FR-3:** Preserve existing custom skills, `AGENTS.md`, Git history, and documentation. Any generated conflict must be inspected and merged deliberately rather than overwritten blindly.
- **FR-4:** Create `.specify/memory/constitution.md` that requires approved requirements, design, non-functional considerations, acceptance criteria, tests where applicable, synchronized documentation, milestone commits, and alternate-model review before every push.
- **FR-5:** Store all new Spec Kit feature artifacts under `docs/features/<NNN>-<slug>/`. Before `$speckit-specify` or any follow-up Spec Kit command, set `SPECIFY_FEATURE_DIRECTORY` to that path; `specs/` must not become a second feature-document source.
- **FR-6:** Align repository instructions with the canonical Spec Kit artifact names: `spec.md` contains requirements and non-functional criteria, `plan.md` contains design and implementation approach, and `tasks.md` is the single numbered task source of truth.
- **FR-7:** Add a durable architecture decision record explaining the integration, directory layout, and excluded optional Git extension.
- **FR-8:** Verify installation, initialized artifact structure, command metadata, and preservation of custom skills. Add automated tests only where project-owned executable logic is added; no new project-owned runtime logic is expected.

## Acceptance criteria

- **AC-1:** `uv --version`, `specify version`, and `specify check` complete successfully after installation.
- **AC-2:** `.specify/` exists and contains shared templates, PowerShell scripts, and a non-template constitution that reflects this repository's governance.
- **AC-3:** `.agents/skills/` contains the existing ten skills plus generated `speckit-*` skills, with no existing skill deleted or replaced unexpectedly.
- **AC-4:** `AGENTS.md` tells agents to use the `speckit-*` workflow and explicitly directs feature artifact creation to `docs/features/<NNN>-<slug>/` through `SPECIFY_FEATURE_DIRECTORY`.
- **AC-5:** `docs/features/` contains this feature's `spec.md`, `plan.md`, and `tasks.md`; no new top-level `specs/` directory is created.
- **AC-6:** The ADR records the accepted choice, alternatives, operational consequences, upgrade procedure, and exit path.
- **AC-7:** The working tree contains only reviewed intended files at each milestone; the changes are committed as coherent milestones.

## Edge cases and assumptions

- The Specify CLI may modify managed context files during initialization. Its diff must be reviewed and repository-specific rules retained.
- `uv` is currently absent. Its installation is machine-wide and authorized by the project maintainer.
- The repository is intentionally initialized without Spec Kit's Git extension because feature-branch automation has not been specified. Existing Git milestone rules remain authoritative.
- `SPECIFY_FEATURE_DIRECTORY` is required because the official default is `specs/<feature>`.

## Non-functional requirements

- **NFR-1 Security:** Use only the official Astral `uv` installer and the `github/spec-kit` release tag `v1.0.3`; do not use an unverified package with the same name.
- **NFR-2 Reproducibility:** Record exact commands and versions in `plan.md`; pin the Specify CLI release.
- **NFR-3 Maintainability:** Keep Spec Kit-generated files separate from custom skills; document the supported upgrade command and review generated diffs before accepting an upgrade.
- **NFR-4 Compatibility:** Use Codex-only integration and PowerShell scripts on Windows; do not require WSL or GitHub Copilot.
- **NFR-5 Reliability:** Do not overwrite existing agent policy or documentation. If initialization cannot merge safely, stop and preserve the repository state.
- **NFR-6 Observability:** Retain validation command output and record any initialization warnings or skipped checks in `plan.md`.
- **NFR-7 Performance and capacity:** Not applicable — this adds development tooling only; normal initialization should complete in under five minutes excluding dependency download time.
- **NFR-8 Privacy, accessibility, localization, and cost:** Not applicable — no deployed user-facing runtime or external paid service is introduced.
