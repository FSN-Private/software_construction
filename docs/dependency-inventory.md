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
