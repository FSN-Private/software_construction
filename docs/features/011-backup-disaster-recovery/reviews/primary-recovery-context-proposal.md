# Primary-only CDK context correction proposal

Status: awaiting specific owner approval after automatic approval review rejection on 2026-10-15. No executable change has been applied.

The owner authorized creation/configuration of missing nonprod renderer resources. Read-only AWS discovery confirms the configured account and ap-southeast-1 region, existing renderer ECR repository, and absent renderer ECS service. The live primary-stack diff fails at requiredContext(recoveryMigrationDigest) even though recovery is not enabled. Additional recovery certificate/build/template/configuration reads are also unconditional in packages/infrastructure/src/app.ts. RecoveryStack and primary recoveryIntegration are both instantiated only when recoveryEnabled is true.

Proposed bounded fix: read and validate those recovery-only contexts only when recoveryEnabled is true. Disabled recovery values remain unused; do not fabricate digests, certificates or approval evidence to get through the current unconditional reads. All recovery-enabled requirements, strict hash/certificate comparisons, offline placeholder restrictions and account/role checks stay unchanged. No IAM expansion, recovery resource enablement, production deployment or qualification waiver is included.

Validation required before publication: primary-only synthesis/diff with documented real bootstrap contexts succeeds without recovery-only inputs; enabled recovery refuses missing or mismatched pins/certificates; existing offline recovery synthesis/contracts pass; independent alternate-model review approves the complete change. Inspect the actual live diff for replacements, data risks, cost-bearing resources and privilege changes before any stack deployment. A renderer service must not be represented as created before CloudFormation confirms it.

Automatic approval review rejected the attempted patch as a persistent deployment-validation change lacking explicit authorization, suggesting supplying required contexts instead. The patch was not applied. Owner approval of this exact opt-in boundary correction is requested before retrying it.
