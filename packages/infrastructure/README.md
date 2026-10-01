# Infrastructure validation

After `npm ci --ignore-scripts`, run `npm run validate` to typecheck, synthesize, assert the CloudFormation template, scan source for secret patterns, inspect the locked dependency tree, validate licenses, and query high-severity runtime vulnerabilities.

Synthesis writes only ignored `dist/` and `cdk.out/` files and performs no AWS deployment. The synth command fixes the non-production context; production and recovery inputs remain external bootstrap work.

## Temporary CDK security patch

[ADR-0013](../../docs/architecture/decisions/0013-temporary-patched-cdk-bundle.md) approves the local `vendor/aws-cdk-lib-2.267.0-brace-expansion-5.0.12.tgz` distribution while upstream CDK still bundles vulnerable `brace-expansion` 5.0.9. Only that bundled package is replaced; CDK's remaining file contents and Apache-2.0/MIT notices are preserved. `vendor/cdk-patch-provenance.json` records official npm input identities/integrities and the approved output checksum. `npm ci --ignore-scripts` installs the patched archive directly; no install script modifies dependencies. `npm run validate:vendor` checks its checksum, lockfile and actual installed version as part of normal validation. Audit and Trivy remain enabled.

To reproduce from the repository root, download official inputs into an ignored directory and rebuild:

```powershell
New-Item -ItemType Directory -Path artifacts/cdk-rebuild -Force
npm pack aws-cdk-lib@2.267.0 brace-expansion@5.0.12 --ignore-scripts --pack-destination artifacts/cdk-rebuild
python packages/infrastructure/scripts/rebuild-cdk-bundle.py artifacts/cdk-rebuild/aws-cdk-lib-2.267.0.tgz artifacts/cdk-rebuild/brace-expansion-5.0.12.tgz artifacts/cdk-rebuild/patched.tgz
```

The canonical archive was built with Python 3.14.7 and zlib `1.3.1.zlib-ng`. The procedure verifies input integrity, every unchanged CDK file, the exact replacement files and the output digest. Different compression implementations may produce different archive bytes; that fails the final checksum and requires review before replacing the approved artifact. To independently verify the checked-in contents without recompressing, use the same command with the vendor archive as output and `--verify-only`.

Remove this archive, provenance and rebuild/validation helpers when an upstream CDK release bundles a fixed version, after clean installation, synthesis/assertions, audit and Trivy pass. Restore an upstream registry dependency and its lockfile in the same change. The original vulnerable bundle is not a suitable security rollback.

For an authorized non-production bootstrap, pass real `ownerEmail`, `alertEmail`, `webOrigin`, `cognitoDomainPrefix`, `monthlyBudgetUsd`, and `deploymentRoleArn` contexts to `cdk diff` and `cdk deploy`, and write the deployment outputs with `--outputs-file`. The stack creates retained encrypted storage and PostgreSQL, immutable API/worker repositories, zero-count ECS bootstrap services, Cloud Map discovery, and an HTTPS API Gateway endpoint. It deliberately omits NAT gateways and load balancers from this low-budget environment; public-address Fargate tasks allow only VPC-link ingress to the API. After setting a permanent Cognito owner password, expose the output values to the protected GitHub environment with `scripts/configure-nonprod-github.ps1`; the script requires the owner username and password in process environment variables and never writes them to repository files.

The generated deployment role can describe, start, and stop only the generated RDS instance. `Deploy non-production` starts the database before migration. `Manage non-production lifecycle` starts or stops RDS and both ECS services manually, and its daily `17:00 UTC` schedule stops compute as a cost safety net. Both workflows serialize through `deploy-nonprod`; retained storage remains billable while compute is stopped.
