# Feature 012 implementation evidence

Approved design and tasks: 2026-10-04. Branch feature/012-versioned-export-basket; base 2680c6a. Planning milestone 36e8347.

- Pinned .NET 10.0.400, Node 24.20.0, npm 11.19.0 available.
- `dotnet restore tests/exports/Platform.Exports.Tests.csproj`: passed; existing dependencies only, vulnerability metadata retrieved.
- `dotnet run --project tests/exports/Platform.Exports.Tests.csproj --no-restore`: passed contract/name/manifest/hash/metadata/purge-scope checks.
- `EXPORT_INTEGRATION=1 dotnet run --project tests/exports/Platform.Exports.Tests.csproj --no-restore`: passed local PostgreSQL migrations and persistent baskets/selection/authorization/revision/hidden/working/quota/no-hold fixtures; MinIO connectivity passed.
- `dotnet build apps/api/Platform.Api.csproj --no-restore --nologo`: passed, zero warnings/errors.
- Local Docker contains feature 008 hardened renderer image; exact approved digest/corpus reproduction still required for T010/T023.

Evidence is incremental. Application transport, full render/preparation, browser, production, feature 011 authoritative purge and remaining acceptance checks are not certified by these foundation checks.
