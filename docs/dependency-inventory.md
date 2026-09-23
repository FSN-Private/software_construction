# Dependency and action inventory

Review date: 2026-09-06. Owner: platform maintainer. Scope: Feature 004 deployable inputs. Every package ecosystem is locked; installations use `npm ci --ignore-scripts` or NuGet locked restore. No third-party install hook is intentionally executed by CI.

## GitHub Actions

| Action | Full SHA / source / license | Permissions, maintenance, and validation |
|---|---|---|
| `actions/checkout` | `de0fac2e4500dabe0009e67214ff5f5447ce83dd`, GitHub-maintained, MIT | Read-only checkout; used with `contents: read`. Pin and absence of mutable refs are contract-tested. |
| `actions/setup-node` | `820762786026740c76f36085b0efc47a31fe5020`, GitHub-maintained, MIT | Installs pinned Node; component lockfile cache only; PR jobs receive no OIDC/secrets. |
| `actions/setup-dotnet` | `a98b56852c35b8e3190ac28c8c2271da59106c68`, GitHub-maintained, MIT | Installs `global.json` SDK; no privileged permissions. |
| `actions/download-artifact`, `upload-artifact` | `d3f86a106a0bac45b974a628896c90dbdf5c8093`, `ea165f8d65b6e75b540449e92b4886f43607fa02`; GitHub-maintained, MIT | Evidence jobs retain read-only token permissions and bind checksummed artifacts to exact source and workflow run IDs. Hosted artifact attestation is unavailable for this private organization plan, so the release contains an in-toto/SLSA provenance statement verified with the manifest instead. |
| `aws-actions/configure-aws-credentials` | `cbe3b392738ccf3f987d68400dafcf4b0624a56c`, AWS-maintained, Apache-2.0 | Protected deployment environments only; exchanges GitHub OIDC for short-lived least-privilege roles; static AWS keys forbidden. |
| `aquasecurity/trivy-action` | `ed142fd0673e97e23eac54620cfb913e5ce36c25`, Aqua Security-maintained, Apache-2.0 | Read-only filesystem scan; PR cache disabled; HIGH/CRITICAL lockfile findings block CI. |

## Packages and images

| Ecosystem / direct input | Source, license, maintenance, transitive impact, and validation |
|---|---|
| NuGet: `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.4 | Microsoft/NuGet, MIT; Microsoft-maintained. `packages.lock.json` records closure; locked restore, API build, authorization tests, and release evidence pass. |
| NuGet storage/API: `AWSSDK.S3` 4.0.102.4 and `Npgsql` 10.0.3 | Official NuGet packages maintained by AWS .NET and Npgsql respectively; Apache-2.0 and PostgreSQL licenses. Required only for Feature 005 S3 object and PostgreSQL metadata boundaries; the API uses them to issue persisted owner-scoped upload grants and never exposes bucket credentials. Locked restore includes NuGet vulnerability metadata. |
| NuGet worker: `Microsoft.Extensions.Hosting` 10.0.0; transitive `AWSSDK.S3` 4.0.102.4 via local `Platform.Storage` | Microsoft/NuGet and the reviewed AWS SDK input above; the worker needs the same locked S3 client only to verify/promote staged objects before metadata activation. Lockfile and vulnerability metadata are verified during restore. |
| npm web: React 19.2.8, React DOM 19.2.8, Vite 8.2.2, TypeScript 6.0.3, Prettier 3.8.2 | npm registry; permissive upstream licenses recorded in `apps/web/package-lock.json`. Lockfile captures closure; format/typecheck/source/accessibility/build and npm advisory audit pass. |
| npm infrastructure: `aws-cdk-lib` 2.267.0, `constructs` 10.8.1, `aws-cdk` 2.1139.0, TypeScript 6.0.3 | npm registry; Apache-2.0/MIT upstreams. Lockfile captures closure; synth/assertion, `npm ls`, secret scan, license validation, and audit pass. Approved transitive exceptions: `BlueOak-1.0.0` (`minimatch`) and MIT alternative of `case`. |
| Container bases | .NET SDK/runtime, Node Alpine, and nginx are exact digest pins; package-definition tests validate immutability. **Image CVE/advisory review remains pending T032**: npm validation does not scan images. |
| Feature 005 integration images | Docker Official Image `postgres:18.1-alpine@sha256:aa6eb304ddb6dd26df23d05db4e5cb05af8951cda3e0dc57731b771e0ef4ab29` and official `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e`; each digest was verified against its named registry before pinning. They are test-only, require no credentials beyond the explicit test values, and must be re-reviewed for CVEs before an image upgrade. |

No unresolved high/critical finding is accepted for the npm/NuGet inputs covered by recorded audits. Before deployment, revalidate action provenance/maintenance, package advisories and patches, image advisories/digests, licenses, and SBOM/provenance. Remove unused inputs and review every upgrade narrowly.

## Feature 007 Windows sync provider

Review date: 2026-09-15. `Microsoft.Data.Sqlite` 10.0.12 and `System.Security.Cryptography.ProtectedData` 10.0.12 are Microsoft-owned, MIT-licensed stable packages from NuGet. SQLite supplies the required crash-safe local replay journal; ProtectedData is the supported .NET wrapper over Windows DPAPI for current-user token custody. Both direct versions and their transitive closures are fixed in `apps/windows-sync/packages.lock.json`; locked restore and vulnerability inspection are release gates. The provider otherwise uses .NET 10 and Windows 11 native `cldapi.dll`, `makeappx.exe`, and `signtool.exe`, avoiding a Cloud Files wrapper, installer framework, UI framework, or signing dependency. Native API boundaries follow the installed Windows SDK 10.0.26100 contract and are exercised only on the isolated trusted Windows host. Remove either NuGet package only if the SQLite journal or DPAPI custody requirement is replaced by an approved ADR.

## Feature 006 graph and manifest implementation

Feature 006 adds no executable dependency. Canonical JSON uses the .NET 10 `System.Text.Json` runtime library, commit identities use `System.Security.Cryptography.SHA256`, graph traversal uses generic collections, and persistence reuses the already locked and reviewed Npgsql 10.0.3 package. This avoids a second serializer or graph library, keeps manifest encoding under direct compatibility fixtures, and adds no license, advisory, install-script, or transitive-package exposure (NFR-003, NFR-014). Reconsider only if measured graph workloads cannot meet NFR-001/NFR-016 with the PostgreSQL indexes and bounded traversal implementation.

## Feature 008 Darktable renderer candidate

Candidate review date: 2026-09-23. `apps/render-worker/Dockerfile` builds darktable 5.6.1 from darktable-org's official release archive, verifies source SHA-256 `e8b84ac98b0b689a244e4036c4b56394c1d58ce2d9abc05e0a060ef9f756dc36`, and pins Ubuntu 24.04 by digest `sha256:008173c23f95b170204355c12626cb5a965d779a7e1283b09e9cffbb1bf33ca3`. The candidate's local OCI manifest is `sha256:01b582e0f7ac9446c7911007db985dccb5d11e058cfee1f6dfa5a304a59ce362`; it starts as UID 65532 and reports `darktable 5.6.1`.

Darktable is GPL-3.0-or-later. The candidate intentionally retains the source-build dependency set (about 610 MB) to provide every required shared library while a minimized runtime image is designed and validated. It is **not approved for deployment**: Docker Scout SBOM/SARIF generation stalled locally without producing output, no vulnerability result exists, the native RAW/XMP corpus has not run, and no owner compatibility approval has been given. These are release-blocking T003 gates; the candidate must be rebuilt, scanned, and published by immutable registry digest before approval.
