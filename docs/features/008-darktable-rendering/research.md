# Research: Darktable Rendering

## Decisions

### Use `darktable-cli` for headless rendering

**Decision**: Invoke the documented `darktable-cli` executable with one immutable original, one explicit XMP sidecar, a private output path, bounded dimensions, and `--library :memory:`.

**Rationale**: The darktable 5.6 manual documents `darktable-cli` as the console export variant, including input/XMP/output and width/height options. An in-memory library prevents a managed request from using or altering a personal library database.

**Alternatives**: GUI automation and in-process Lua were rejected. GUI automation is not suitable for workers; the manual calls Lua library use experimental.

### Pin an artifact digest, not a tag

**Decision**: The deployment image must use a reviewed darktable release artifact and immutable OCI digest, recorded with source URL, SHA-256, license, SBOM, vulnerability scan, and corpus result before it enters the compatibility matrix.

**Rationale**: A version tag can be retargeted and does not itself demonstrate the provenance of a native rendering executable.

**Alternatives**: Floating tags and distribution package installation during worker startup are rejected because both weaken reproducibility and increase supply-chain exposure.

### Fail closed when a version cannot be resolved

**Decision**: A historical request requires a feature-006 adapter result containing the exact selected version and XMP content identity. Until feature 006 exists, historical requests return `unavailable/version_resolution_unavailable`.

**Rationale**: A current edit or branch head may look plausible but cannot prove historical identity.

**Alternatives**: Current-XMP fallback and synthetic test history are rejected for production behavior.

### Publish only validated derivative/provenance pairs

**Decision**: Validate output dimensions, type, size, and SHA-256 before writing a private derivative; then commit the completed rendition and full provenance in one transaction.

**Rationale**: Any independently visible output could otherwise be mistaken for a different version.

**Alternatives**: Async provenance and mutable cache metadata are rejected.

## Validation evidence to obtain before release

- Official darktable 5.6 CLI documentation: [darktable-cli](https://docs.darktable.org/usermanual/5.6/en/special-topics/program-invocation/darktable-cli/).
- A reviewer must record the exact artifact source/digest and the checked license before image publication.
- The trusted Linux runner must execute current plus three historical XMP corpus revisions and retain output/provenance comparison results.
