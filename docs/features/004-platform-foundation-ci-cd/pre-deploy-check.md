# Feature 004 pre-deploy checklist

Status: pre-AWS repository evidence complete; account-specific verification is pending explicit AWS authorization.

| Check | Evidence / required action | Status |
|---|---|---|
| Region | `ap-southeast-1` is the configured non-production target; confirm ECS, Cognito, Budgets, SNS, KMS, CloudWatch, and required quotas in the chosen account. | Pending account access |
| Quotas | Confirm VPC, NAT gateway, EIP, ECS/Fargate, ECR, Cognito, SNS, and CloudWatch quotas before deploy. | Pending account access |
| Cost | Establish account-wide budget/anomaly alerts before CDK bootstrap; estimate NAT gateway, logs, ECR, and ECS costs and record an owner-approved cap. | Pending account access |
| Action pins | Full SHA pins are contract-tested; re-check upstream action commit provenance immediately before first external deployment. | Repository check complete |
| Packages/images | Lockfiles and Docker digests are validated locally; re-run security/package-definition checks immediately before deploy. | Repository check complete |
| Code signing | Windows package is unsigned. Do not distribute it; select an owner-controlled signing key/custody process before feature 007 or public distribution. | Deferred by product scope |
| Rollback | Verify retained evidence, compatible task definition, protected production environment, and forward-recovery owner before production. | Pending environment bootstrap |
