# Dependency and action inventory

Review date: 2026-09-06. Owner: platform maintainer. Scope: Feature 004 deployable inputs. Every package ecosystem is locked; installations use `npm ci --ignore-scripts` or NuGet locked restore. No third-party install hook is intentionally executed by CI.

## GitHub Actions

| Action | Full SHA / source / license | Permissions, maintenance, and validation |
|---|---|---|
| `actions/checkout` | `de0fac2e4500dabe0009e67214ff5f5447ce83dd`, GitHub-maintained, MIT | Read-only checkout; used with `contents: read`. Pin and absence of mutable refs are contract-tested. |
| `actions/setup-node` | `820762786026740c76f36085b0efc47a31fe5020`, GitHub-maintained, MIT | Installs pinned Node; component lockfile cache only; PR jobs receive no OIDC/secrets. |
| `actions/setup-dotnet` | `a98b56852c35b8e3190ac28c8c2271da59106c68`, GitHub-maintained, MIT | Installs `global.json` SDK; no privileged permissions. |
| `actions/download-artifact`, `upload-artifact`, `attest-build-provenance` | `d3f86a106a0bac45b974a628896c90dbdf5c8093`, `ea165f8d65b6e75b540449e92b4886f43607fa02`, `977bb373ede98d70efdf65b84cb5f73e068dcc2a`; GitHub-maintained, MIT | Evidence jobs have narrowly scoped artifact/attestation permissions and bind artifacts to exact run IDs/SHA. |
| `aws-actions/configure-aws-credentials` | `cbe3b392738ccf3f987d68400dafcf4b0624a56c`, AWS-maintained, Apache-2.0 | Protected deployment environments only; exchanges GitHub OIDC for short-lived least-privilege roles; static AWS keys forbidden. |
| `aquasecurity/trivy-action` | `ed142fd0673e97e23eac54620cfb913e5ce36c25`, Aqua Security-maintained, Apache-2.0 | Read-only filesystem scan; PR cache disabled; HIGH/CRITICAL lockfile findings block CI. |

## Packages and images

| Ecosystem / direct input | Source, license, maintenance, transitive impact, and validation |
|---|---|
| NuGet: `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.4 | Microsoft/NuGet, MIT; Microsoft-maintained. `packages.lock.json` records closure; locked restore, API build, authorization tests, and release evidence pass. |
| npm web: React 19.2.8, React DOM 19.2.8, Vite 8.2.2, TypeScript 6.0.3, Prettier 3.8.2 | npm registry; permissive upstream licenses recorded in `apps/web/package-lock.json`. Lockfile captures closure; format/typecheck/source/accessibility/build and npm advisory audit pass. |
| npm infrastructure: `aws-cdk-lib` 2.267.0, `constructs` 10.8.1, `aws-cdk` 2.1139.0, TypeScript 6.0.3 | npm registry; Apache-2.0/MIT upstreams. Lockfile captures closure; synth/assertion, `npm ls`, secret scan, license validation, and audit pass. Approved transitive exceptions: `BlueOak-1.0.0` (`minimatch`) and MIT alternative of `case`. |
| Container bases | .NET SDK/runtime, Node Alpine, and nginx are exact digest pins; package-definition tests validate immutability. **Image CVE/advisory review remains pending T032**: npm validation does not scan images. |

No unresolved high/critical finding is accepted for the npm/NuGet inputs covered by recorded audits. Before deployment, revalidate action provenance/maintenance, package advisories and patches, image advisories/digests, licenses, and SBOM/provenance. Remove unused inputs and review every upgrade narrowly.
