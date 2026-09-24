# ADR-0011: Paused renderer candidate acceptance

- Status: Accepted
- Date: 2026-09-24
- Decision owner: Feature 008 product owner
- Feature: [008 Darktable Rendering](../../features/008-darktable-rendering/spec.md)

## Context

The deployed API must bind rendition intake to the exact renderer digest. A candidate cannot be marked active before its trusted-Linux corpus and live HTTP checks pass, but those HTTP checks need to exercise the deployed candidate. Updating the sole active compatibility row before the checks would make an ECS rollback to the prior API incompatible with the database.

## Decision

Keep the active compatibility row unchanged while a candidate is admitted. Admission requires a release-injected trusted-Linux evidence identifier and the configured Cognito operator username; it pauses general intake and permits only the authenticated fixture owner. The live fixture verifies uncached request start/render completion and 20 cached private-locator samples. Only `activate`, after that fixture, changes the active compatibility row and resumes general intake. `abort` rejects the candidate and clears the acceptance session before paired ECS rollback.

## Alternatives considered

- Promote before HTTP acceptance: rejected because a failed fixture breaks rollback.
- Permit any authenticated owner to promote: rejected because ordinary owner tokens could activate an unverified digest.
- Validate outside the API: rejected because it would not measure the owner-visible request and private-derivative path.

## Consequences

The non-production environment must retain one authorized RAW/current-XMP fixture and its stable identity as protected configuration. Candidate records are audit evidence, not general compatibility approval. A failed acceptance can leave no user-visible candidate output and restores the prior active compatibility row. Production remains blocked by ADR-0009 and T021.
