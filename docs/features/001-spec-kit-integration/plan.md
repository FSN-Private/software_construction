# Implementation plan - Spec Kit integration

**Status:** Complete; pre-push review remains required only if a push is requested
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

## Execution record

- 2026-09-02: Installed `uv 0.12.9` with Astral's official installer. The installer was downloaded to a temporary file and inspected before execution.
- 2026-09-02: Installed `specify-cli 1.0.3` from `git+https://github.com/github/spec-kit.git@v1.0.3`. The resolved source commit was `6906bc582230bb752776e23287ee97990c1af743`.
- 2026-09-02: Verified `uv --version`, `specify version`, and `specify check`; the latter detected Codex CLI as available.
- 2026-09-02: Ran `specify init --here --force --integration codex --script ps` in a clean repository. It created `.specify/` and ten `speckit-*` skills without modifying `AGENTS.md`, deleting existing skills, or creating `specs/`.
- 2026-09-02: Validated 20 skill frontmatter blocks, parsed all generated PowerShell scripts, verified `specify version --features` and `specify check`, and confirmed that no top-level `specs/` directory exists.
- 2026-09-02: Unit tests are not applicable: this integration adds generated Spec Kit workflows, documentation, and policy only; it adds no project-owned runtime or executable logic. PowerShell syntax parsing and Specify CLI checks provide the relevant automated validation.

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
