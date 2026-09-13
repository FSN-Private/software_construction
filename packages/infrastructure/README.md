# Infrastructure validation

After `npm ci --ignore-scripts`, run `npm run validate` to typecheck, synthesize, assert the CloudFormation template, scan source for secret patterns, inspect the locked dependency tree, validate licenses, and query high-severity runtime vulnerabilities.

Synthesis writes only ignored `dist/` and `cdk.out/` files and performs no AWS deployment. The synth command fixes the non-production context; production and recovery inputs remain external bootstrap work.

For an authorized non-production bootstrap, pass real `ownerEmail`, `alertEmail`, `webOrigin`, `cognitoDomainPrefix`, `monthlyBudgetUsd`, and `deploymentRoleArn` contexts to `cdk diff` and `cdk deploy`, and write the deployment outputs with `--outputs-file`. The stack creates retained encrypted storage and PostgreSQL, immutable API/worker repositories, zero-count ECS bootstrap services, Cloud Map discovery, and an HTTPS API Gateway endpoint. It deliberately omits NAT gateways and load balancers from this low-budget environment; public-address Fargate tasks allow only VPC-link ingress to the API. After setting a permanent Cognito owner password, expose the output values to the protected GitHub environment with `scripts/configure-nonprod-github.ps1`; the script requires the owner username and password in process environment variables and never writes them to repository files.
