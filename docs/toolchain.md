# Contributor toolchain

This repository pins .NET SDK `10.0.400` and Node.js `24.20.0` LTS. Install the SDK and runtime before running executable validation. The repository does not vendor either toolchain.

Use `dotnet --version` and `node --version` to verify the installed versions. `global.json` accepts only the pinned .NET feature band and its servicing patches; `.nvmrc` identifies the required Node release.

Toolchain changes are security-sensitive dependency changes. Update them only with their release notes, compatibility evidence, and lockfile/package review in the relevant feature work.
