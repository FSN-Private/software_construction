# Non-production bootstrap runbook

Owner: platform maintainer. This runbook authorizes no AWS or GitHub mutation by itself.

1. Use a dedicated non-production account and approved region. Enable MFA, CloudTrail, billing alerts, and a low account-wide budget before CDK bootstrap.
2. Create the protected GitHub `nonprod` environment, a `PROTECTION_AUDIT_TOKEN` fine-grained token limited to this repository with Actions read-only (Metadata read is automatic; no Administration or write access), and a least-privilege OIDC role. The issuing user or GitHub App installation must have repository access sufficient to disclose ruleset bypass actors; the audit fails closed if GitHub omits them. Do not grant write access just to make it pass. Bind the role's `sub` claim to the current repository owner/name and `environment:nonprod`; repository transfers require updating that trust condition. Never add static AWS keys.
3. Record `webOrigin`, a unique Cognito domain prefix, owner/alert email, account ID, region, deployment-role ARN, and budget in the pre-deploy checklist.
4. Run `cdk diff` with those contexts before `cdk deploy`; inspect every cost-bearing resource. Deploy with `--outputs-file` so the generated ECS/ECR, HTTPS endpoint, and Cognito identifiers remain machine-readable. Confirm SNS subscriptions before relying on alerts.
5. Set a permanent password for the provisioned Cognito owner. Put the username/password in the process environment and run `scripts/configure-nonprod-github.ps1` against the CDK output file after explicit GitHub-configuration authorization. The script writes generated identifiers as `nonprod` environment variables and sends the two credentials directly to GitHub secrets.
6. After explicit deployment authorization, dispatch `Deploy non-production` with the successful release-evidence run ID and record its evidence. Retain durable resources; destroy only explicitly identified test-only resources after the observation window.
