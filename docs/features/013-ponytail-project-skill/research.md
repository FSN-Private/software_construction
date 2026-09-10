# Research: Project-Scoped Ponytail Skills

## Installation form

**Decision**: Vendor only the six upstream `skills/*/SKILL.md` files into `.agents/skills/`.

**Rationale**: This is the narrowest installation that satisfies project scope. The local Codex CLI's plugin commands manage marketplace/plugin state outside the repository, while Ponytail's full plugin uses Node.js lifecycle hooks and Node.js is unavailable in the current environment.

**Alternatives considered**: Full marketplace plugin, root `AGENTS.md` merge, and whole-repository vendoring. Each expands scope or trust without adding required project-local skill behavior.

## Source identity and version

**Decision**: Use the authoritative repository `DietrichGebert/ponytail`, release `v4.8.4`, commit `bc9ee949d5f439e8b9f3bb92c6d6d3d1e6ebd324`.

**Rationale**: `git ls-remote` and a detached shallow checkout confirmed that the tag resolves to this immutable commit. The inspected Codex manifest also declares version `4.8.4`, repository identity, and MIT license.

**Alternatives considered**: Upstream `main` resolved to a newer mutable commit and was rejected for reproducibility.

## Supply-chain assessment

**Decision**: Treat the instruction text as reviewed third-party content but exclude every executable or package surface.

**Rationale**: The selected files are Markdown with YAML front matter and have no transitive packages, build scripts, install scripts, runtime permissions, or network access. Known executable-package advisories are therefore not applicable to the copied artifact. The upstream MIT notice permits redistribution when retained.

**Update strategy**: Select a new immutable release, review upstream ownership and maintenance, inspect the six-file diff plus any license change, regenerate SHA-256 hashes, run validation, and commit as a separate reviewed change.

**Removal strategy**: Delete the six vendored skill directories and the feature's `third-party.md`; no runtime or global state needs removal.
