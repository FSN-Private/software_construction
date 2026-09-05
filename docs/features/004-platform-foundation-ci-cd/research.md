# Research: Platform Foundation and CI/CD

**Feature**: [spec.md](spec.md) | **Date**: 2026-09-03 | **Status**: Complete for planning

1. **Managed modular platform** — use an ASP.NET Core API, React shell, AWS CDK v2, ECS Fargate, Cognito, RDS PostgreSQL, S3/ECR, and CloudWatch/OpenTelemetry. This matches the program research while avoiding premature microservices or Kubernetes.
2. **Build once, promote by digest** — GitHub Actions builds one evidence-backed release manifest, deploys its digests to non-production, and promotes the same digests only after protected approval. Rebuild-at-deployment and long-lived AWS keys are rejected.
3. **Conservative trust boundary** — untrusted PRs receive no protected secret, environment, trusted publication ability, OIDC identity, or privileged runner. Actions use least permissions and immutable full-SHA pins.
4. **Forward-recovery migrations** — use expand/migrate/contract. Rollback is allowed only while the target application is compatible with current state; otherwise the release pauses for forward recovery.
5. **Cost envelope** — use USD 450–950/month production and USD 100–250/month scheduled non-production as planning ranges; an AWS Pricing Calculator estimate is required before production.

Evidence, alternatives, and version-sensitive validation gates are retained in the parent [research.md](../003-darktable-version-control/research.md#decision-8-deploy-on-aws-using-managed-boundaries) and [ADR-0003](../../architecture/decisions/0003-platform-foundation-and-delivery.md).
