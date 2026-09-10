# Infrastructure validation

After `npm ci --ignore-scripts`, run `npm run validate` to typecheck, synthesize, assert the CloudFormation template, scan source for secret patterns, inspect the locked dependency tree, validate licenses, and query high-severity runtime vulnerabilities.

Synthesis writes only ignored `dist/` and `cdk.out/` files and performs no AWS deployment. The synth command fixes the non-production context; production and recovery inputs remain external bootstrap work.
