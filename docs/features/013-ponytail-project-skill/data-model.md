# Data Model: Project-Scoped Ponytail Skills

No application or runtime data is introduced. The repository stores two documentation entities:

## Vendored Skill

- **Identity**: Skill name from YAML front matter.
- **Path**: `.agents/skills/<name>/SKILL.md`, where the directory matches the name.
- **Content**: Exact upstream v4.8.4 bytes.
- **Integrity**: SHA-256 recorded in `third-party.md`.
- **Lifecycle**: Added, explicitly updated to another reviewed immutable release, or removed.

## Provenance Record

- **Source**: Authoritative repository URL.
- **Version**: Release tag and immutable commit.
- **Inventory**: Included upstream and destination paths.
- **Integrity**: Per-file SHA-256 values.
- **License**: Complete upstream MIT notice.
- **Operations**: Validation, update, and removal procedures.

There are no relationships to application data and no runtime state transitions.
