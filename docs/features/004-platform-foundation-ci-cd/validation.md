# Feature 004 validation record

## Pre-AWS validation -- 2026-09-06

Executed in the pinned repository workspace without AWS or GitHub deployment credentials:

- `npm run validate:toolchain` -- passed.
- `npm run test:reliability` -- passed; simulates superseded CI cancellation, duplicate production delivery queuing, runner-loss timeout bounds, compatible rollback binding, incompatible rollback refusal, unattributed task-definition refusal, and no-rebuild recovery contracts.
- `npm run test:ci-contract` and `npm run test:release-evidence` -- passed.
- `npm run validate:security` -- passed after an initial sandbox advisory-endpoint outage; the normal-network retry reported no dependency audit, secret scan, dependency-tree, or license failure.

## Pending live evidence

This is not a clean-machine or AWS exercise. Before marking T030 complete, run the quickstart from a fresh supported machine and record toolchain/install command output. Before marking T032 complete, record the selected account/region quota and pricing evidence, then complete the non-production deployment, owner authentication, alert/budget, recreation, and rollback exercises without production mutation.
