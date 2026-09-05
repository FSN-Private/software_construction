# Platform Foundation Validation Quickstart

Install the pinned .NET 10 SDK and Node 24 runtime specified by the repository toolchain. Copy only documented non-secret example configuration; local validation requires no AWS or GitHub credential.

After implementation supplies its scripts, run the documented repository validation command to execute formatting/lint, static/type checks, unit/integration tests, contract fixtures, build/package, CDK synthesis, and security/dependency review. Seed a failure in each category and verify its local name matches the stable CI category.

Start the API and web shell, sign in using the local test identity, and verify authenticated `/v1/session`, `/v1/version`, and `/ready`. Confirm `/live` reveals no dependency detail, an unauthenticated protected call receives a generic versioned denial, and one operation ID joins web/API traces without recording seeded prohibited values.

After explicit authorization and bootstrap inputs, verify a non-production deployment against [health-and-status-v1.md](contracts/health-and-status-v1.md), compare its manifest digests with production-promotion evidence, inject a health failure, and exercise the compatible rollback path. Alert and budget exercises occur only outside production.
