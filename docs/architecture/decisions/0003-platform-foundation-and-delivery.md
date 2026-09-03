# ADR-0003: Platform Foundation and Immutable Delivery

**Date**: 2026-09-03  
**Status**: Proposed  
**Owners**: Project maintainer  
**Feature**: [004 Platform Foundation and CI/CD](../../features/004-platform-foundation-ci-cd/spec.md)

## Context

Subsequent features need secure identity, deployment, observability, and artifact-provenance boundaries. The program requires AWS, GitHub Actions, private single-owner access, regional recovery, and data-safe rollback.

## Decision

Use .NET 10/C# 14 for the API, Node 24/TypeScript 6/React 19 for the web shell, AWS CDK v2, ECS Fargate, Cognito, RDS PostgreSQL, S3/ECR, and CloudWatch/OpenTelemetry. Separate non-production, production, and recovery accounts. GitHub Actions builds immutable release manifests with checksums, SPDX SBOMs, and provenance; GitHub OIDC deploys identical digests after protected approval. Migrations use expand/migrate/contract; incompatible rollback is refused in favor of forward recovery.

## Alternatives

- Kubernetes/EKS: disproportionate operational complexity for v1.
- Lambda/serverless-only: unsuitable for later isolated native render execution.
- Long-lived AWS keys or rebuild-at-deploy: weaken attribution and rollback.
- Automatic production promotion: violates the required versioned approval.

## Consequences

The decision creates consistent, reviewable boundaries but needs AWS accounts, OIDC bootstrap, budget approval, and version revalidation before external mutation. The CDK/Actions estate needs maintenance. Artifacts, contracts, and manifests remain portable; replacing ECS/CDK requires a controlled infrastructure migration. This proposed ADR authorizes no production deployment.
