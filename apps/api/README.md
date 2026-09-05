# Platform API validation

The API is built with .NET SDK analyzers enabled and warnings treated as errors.

From the repository root:

- `npm run format` checks backend formatting.
- `npm run lint` runs static analysis.
- `npm run test` runs the API foundation unit harness and release-tool tests.
- `npm run test:contract` validates shared v1 contracts.

Authenticated endpoint integration tests are added with the API contract in T022/T026; no placeholder endpoint is treated as integration coverage.
