# Non-production bootstrap runbook

Owner: platform maintainer. This runbook authorizes no AWS or GitHub mutation by itself.

1. Use a dedicated non-production account and approved region. Enable MFA, CloudTrail, billing alerts, and a low account-wide budget before CDK bootstrap.
2. Create the protected GitHub `nonprod` environment, read-only `PROTECTION_AUDIT_TOKEN`, and least-privilege OIDC role. Never add static AWS keys.
3. Record `webOrigin`, a unique Cognito domain prefix, owner/alert email, account ID, region, ECS/ECR identifiers, and budget in the pre-deploy checklist.
4. Run `cdk diff` before `cdk deploy`; inspect every cost-bearing resource. Confirm SNS subscriptions before relying on alerts.
5. After explicit authorization, execute the Phase 5 non-production exercise and record its evidence. Destroy test-only resources when the observation window ends.
