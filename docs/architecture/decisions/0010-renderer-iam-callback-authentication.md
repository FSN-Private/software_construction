# ADR-0010: Authenticate renderer callbacks with the ECS task role

**Date**: 2026-09-24

**Status**: Accepted

**Decision owner**: Fredrik Snårbacka

**Feature**: [008 Darktable Rendering](../../features/008-darktable-rendering/spec.md)

**Extends**: [ADR-0007](0007-isolated-darktable-rendering-provenance.md)

## Context

The renderer must consume SQS work and report a completed private derivative or a safe failure to the API without PostgreSQL credentials or a long-lived shared secret. The existing rendition routes use Cognito owner authentication and therefore cannot be reused by a machine workload.

## Decision

The renderer ECS task role signs requests to dedicated internal callback routes with AWS Signature Version 4. API Gateway applies `AWS_IAM` authorization to those routes, and the renderer role receives `execute-api:Invoke` only for the callback route ARN. The API validates the authenticated role identity and verifies the callback against the durable request and attempt before it changes any render state.

The job envelope contains only immutable IDs, content hashes, object versions, rendition parameters, and the canonical private derivative key. It contains no database credential, owner token, local path, or mutable source reference. The API sends jobs; the renderer only consumes/deletes queue messages, reads immutable input, writes the designated derivative, and calls the internal routes.

## Options considered

- Shared bearer token: rejected because rotation and accidental disclosure would create a broad, long-lived worker credential.
- Cognito user token: rejected because a renderer is not an owner and should not impersonate one.
- Direct worker database writes: rejected by ADR-0007 because it expands the native-renderer trust boundary.

## Consequences

- Callback routes are separate from owner-facing API routes and must never accept Cognito-owner credentials as worker authority.
- The IAM policy, API Gateway route authorization, callback identity validation, queue envelope, and failure handling require contract and integration coverage.
- Removing the renderer means removing the dedicated callback routes and its narrowly-scoped task-role permission; no database secret rotation is required.

## Follow-up

Tasks T022–T024 implement and validate this boundary. T021 remains the independent production-vulnerability gate.
