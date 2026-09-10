# Software Construction

This repository is building the darktable version-controlled image-library platform described in [feature 003](docs/features/003-darktable-version-control/spec.md). Feature work is tracked under `docs/features/` and is implemented only on its matching feature branch.

## Local validation

Install the versions pinned in [docs/toolchain.md](docs/toolchain.md), then run:

```powershell
npm run validate:toolchain
```

Install each implemented Node package before running its command:

```powershell
npm --prefix apps/web ci
npm --prefix packages/infrastructure ci
```

The root command names are stable and will gain their implementations with their owning tasks:

- `npm run format`, `npm run lint`, `npm run typecheck`
- `npm run test`, `npm run test:integration`, `npm run test:contract`
- `npm run build`, `npm run validate:infrastructure`, `npm run validate:security`
- `npm run package:api-container`, `npm run package:web-container`, `npm run package:infrastructure-container`, `npm run package:windows`
- `npm run validate` runs the complete suite once every category is registered.

Until those implementation tasks are complete, unregistered category commands intentionally fail rather than give a false passing result. CI uses the same command names.

Container package commands require Docker Desktop or another Docker-compatible engine. The definitions pin all base images by digest; run `npm run test:package-definitions` to validate their locked build contract without Docker.

The scheduled protection audit requires a repository secret named `PROTECTION_AUDIT_TOKEN`: a fine-grained GitHub token restricted to this repository with **Actions: read-only**. Fine-grained token metadata read access is included automatically; do not grant Administration or write access. The issuing user or GitHub App installation must retain repository access sufficient for GitHub to disclose ruleset bypass actors; GitHub can omit that data from a low-privilege credential. In either case the audit fails closed rather than reporting a passing protection check. Do not increase this token's permissions merely to clear the failure—select and document an owner-controlled audit mechanism that can inspect bypasses first. The token is an external GitHub setup step and is never available to pull-request CI.
