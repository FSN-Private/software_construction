# Implementation plan — Spec Kit integration

**Status:** Approved for execution
**Specification:** [spec.md](spec.md)
**ADR:** [../../architecture/decisions/0001-adopt-spec-kit.md](../../architecture/decisions/0001-adopt-spec-kit.md)

## Technical approach

Use the official GitHub Spec Kit release `v1.0.3` and its Codex integration. Install the persistent CLI through Astral `uv`, then initialize the existing repository with:

```powershell
specify init --here --force --integration codex --script ps
```

Inspect the generated diff before accepting it. Preserve repository-owned `AGENTS.md` instructions and custom `.agents/skills/*` directories. Configure the Spec Kit constitution and repository routing so every feature sets `SPECIFY_FEATURE_DIRECTORY=docs/features/<NNN>-<slug>` before invoking `$speckit-specify`, `$speckit-plan`, `$speckit-tasks`, or other feature-scoped commands. This keeps canonical Spec Kit artifact names in the required `docs/` hierarchy.

Spec Kit's optional Git extension is deliberately excluded. Existing milestone commits and pre-push policy remain in force.

## Validation strategy

- Verify command availability with `uv --version`, `specify version`, and `specify check`.
- Inspect the generated `.specify/` tree and all new `speckit-*` skills.
- Confirm existing custom skill directory names remain present.
- Confirm no `specs/` directory was created.
- Run `git diff --check` and inspect every staged file before each milestone commit.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Initializer changes `AGENTS.md` or managed files unexpectedly | Run only after a clean-state check; inspect and reconcile the diff before staging. |
| Default Spec Kit location creates `specs/` | Establish and document the explicit `SPECIFY_FEATURE_DIRECTORY` convention; remove only an accidentally generated empty directory after confirming it is safe. |
| Upgrades overwrite local customizations | Use `specify integration upgrade codex`, review its diff, and keep repository policy in `AGENTS.md` and constitution rather than generated command files. |
| Tool provenance changes | Pin `v1.0.3` and use the official sources recorded in the specification. |

## Milestones

1. Document and approve the integration design, canonical artifact mapping, and ADR.
2. Install verified tooling and initialize Spec Kit for Codex.
3. Reconcile governance, validate the initialized repository, and commit the completed integration.
