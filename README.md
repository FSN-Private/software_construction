# Software Construction

This repository is building the darktable version-controlled image-library platform described in [feature 003](docs/features/003-darktable-version-control/spec.md). Feature work is tracked under `docs/features/` and is implemented only on its matching feature branch.

## Local validation

Install the versions pinned in [docs/toolchain.md](docs/toolchain.md), then run:

```powershell
npm run validate:toolchain
```

The root command names are stable and will gain their implementations with their owning tasks:

- `npm run format`, `npm run lint`, `npm run typecheck`
- `npm run test`, `npm run test:integration`, `npm run test:contract`
- `npm run build`, `npm run validate:infrastructure`, `npm run validate:security`
- `npm run validate` runs the complete suite once every category is registered.

Until those implementation tasks are complete, unregistered category commands intentionally fail rather than give a false passing result. CI uses the same command names.
