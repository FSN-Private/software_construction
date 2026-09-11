# Feature 004 pre-deploy checklist

Status: pre-AWS repository evidence complete; account-specific verification is pending explicit AWS authorization. GitHub active-ruleset verification is supported, but production promotion remains blocked until the repository has an enforceable private-repository reviewer gate.

| Check | Evidence / required action | Status |
|---|---|---|
| Region | `ap-southeast-1` is the configured non-production target; confirm ECS, Cognito, Budgets, SNS, KMS, CloudWatch, and required quotas in the chosen account. | Pending account access |
| Quotas | Confirm VPC, NAT gateway, EIP, ECS/Fargate, ECR, Cognito, SNS, and CloudWatch quotas before deploy. | Pending account access |
| Cost | The owner set an initial non-production alert threshold of USD 20/month on 2026-09-11. The stack omits NAT gateways and load balancers, but always-on PostgreSQL plus two Fargate tasks may still exceed the threshold; obtain a live Pricing Calculator estimate and explicit acceptance before deployment. | Pending account access and cost acceptance |
| Action pins | Full SHA pins are contract-tested; re-check upstream action commit provenance immediately before first external deployment. | Repository check complete |
| Packages/images | Lockfiles and Docker digests are validated locally; re-run security/package-definition checks immediately before deploy. | Repository check complete |
| Delivery controls | The active `master` ruleset requires the six stable CI checks and the `nonprod` environment restricts deployments to protected branches. The OIDC trust is bound to `FSN-Private/software_construction:environment:nonprod`. The audit rejects an unreadable ruleset bypass list. | Non-production bootstrap evidence recorded; select an owner-controlled bypass-audit mechanism and execute the live audit before deployment |
| Production approval | The private repository's current GitHub plan does not expose required environment reviewers. Do not create a production AWS deployment role or deploy production until an enforceable approval control is available and independently verified. | Blocked by GitHub plan capability |
| Code signing | Windows package is unsigned. Do not distribute it; select an owner-controlled signing key/custody process before feature 007 or public distribution. | Deferred by product scope |
| Rollback | Verify retained evidence, compatible task definition, protected production environment, and forward-recovery owner before production. | Pending environment bootstrap |
| Non-production identity | Owner and alert email: `fredrik.snarbacka@gmail.com`. Cognito domain prefix: `software-construction-nonprod-344531454551`. | Owner supplied; global prefix availability pending AWS check |
| Web origin | No hosted origin exists yet. Use `http://localhost:5173` temporarily for the non-production Cognito callback and CORS allowlist, then replace it before hosting the web client. | Owner supplied absence; temporary local origin documented |
| Bootstrap authentication | Use AWS IAM Identity Center with MFA and a short-lived AWS CLI v2 SSO profile for the initial CDK bootstrap; retain GitHub OIDC for release deployments. | SSO profile and CLI installation pending |
