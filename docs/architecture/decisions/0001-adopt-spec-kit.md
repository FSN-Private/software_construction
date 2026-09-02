# ADR-0001: Adopt GitHub Spec Kit with Codex and documentation-local artifacts

- Status: Accepted
- Date: 2026-09-02
- Decision owners: Project maintainer
- Related feature: [Spec Kit integration](../../features/001-spec-kit-integration/spec.md)

## Context

The repository already requires specification-first development, numbered execution tasks, synchronized documentation and tests, readable milestone commits, and an alternate-model pre-push review. GitHub Spec Kit provides executable specification, planning, tasking, analysis, implementation, and convergence workflows for Codex, but its default feature path is `specs/<feature>` and its default Git workflow is optional.

## Decision

Adopt GitHub Spec Kit `v1.0.3` with the Codex-only integration and PowerShell scripts. Preserve existing custom skills. Store canonical Spec Kit feature artifacts in `docs/features/<NNN>-<slug>/` by explicitly setting `SPECIFY_FEATURE_DIRECTORY` before feature-scoped commands. Use `spec.md` for requirements and non-functional criteria, `plan.md` for design and technical approach, and `tasks.md` as the numbered implementation source of truth.

Do not install Spec Kit's optional Git extension at this time. The repository's existing milestone-commit and pre-push-review workflow remains governing policy.

## Alternatives considered

1. Keep the current four-document convention and add parallel Spec Kit artifacts. Rejected because duplicated requirements and task lists would create conflicting sources of truth.
2. Use Spec Kit's default `specs/` directory. Rejected because the project requires all specifications under `docs/`.
3. Install Codex and GitHub Copilot integrations. Rejected for now because the project uses Codex only; another integration can be added later with its own documented decision.
4. Enable the optional Git extension. Deferred because branch naming and automatic branch creation have not been approved.
5. Do not adopt Spec Kit. Rejected because it would leave the existing process manual rather than providing reusable Codex workflows.

## Consequences

Positive consequences:

- Codex receives standard `$speckit-*` workflows while retaining project-specific quality gates.
- Requirements, design, non-functional criteria, and tasks have one canonical location under `docs/`.
- The Specify CLI version is pinned and its upgrade path is auditable.

Negative consequences and risks:

- Feature commands require the explicit `SPECIFY_FEATURE_DIRECTORY` convention; forgetting it can create an unwanted `specs/` directory.
- Spec Kit upgrades may change managed files, requiring diff review and reconciliation.
- A machine-local CLI installation must be maintained separately from the repository.

## Security and operations

Install only from Astral's official `uv` distribution and `github/spec-kit@v1.0.3`. Review initializer and upgrade diffs before committing. Do not expose credentials in commands, configuration, or generated documentation. The integration has no production deployment, data migration, or user-facing operational impact.

## Exit and rollback

To stop using Spec Kit, remove its generated `.specify/` and `speckit-*` skill artifacts in a reviewed commit while preserving the repository-owned docs and custom skills. Uninstall the local Specify CLI separately with `uv tool uninstall specify-cli` if it is no longer needed. This ADR remains historical evidence even if the integration is removed.
