# Release provenance build type v1

This document defines the project-owned SLSA provenance `buildType` emitted by `.github/workflows/build-release.yml` for a successful `workflow_run` of `Continuous integration` on `master`.

`externalParameters` contains the exact `sourceRevision`, numeric triggering `ciRunId`, `repository` in `owner/name` form, and workflow path `.github/workflows/build-release.yml`. `resolvedDependencies` contains one `git+https` URI for that repository and `refs/heads/master`, with `digest.gitCommit` equal to `sourceRevision`.

`runDetails.builder.id` identifies the same repository, workflow path, and `master` ref. `runDetails.metadata.invocationId` identifies the release-evidence workflow run. Every subject names one release artifact and its SHA-256 digest. The verifier receives the expected source revision, repository, and, for deployment, release run ID from GitHub workflow context rather than trusting those values from the statement.

The statement and release manifest are retained together in the access-controlled GitHub Actions artifact. This build type provides internally verifiable provenance for the current private-repository plan; it is not a GitHub-hosted Sigstore attestation.
