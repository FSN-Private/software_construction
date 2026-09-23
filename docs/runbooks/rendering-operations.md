# Rendering operations

## Pause, drain, or cancel

Set render intake to paused before maintenance. Let running attempts finish; cancel only pending attempts. A restart must mark an attempt that remains running after ten minutes as retryable or terminal. Completed derivatives and provenance remain valid.

## Failed or stuck render

Use the opaque request and attempt IDs. Check queue age, worker duration, failure code, retry count, renderer matrix, and storage health. Never request or paste worker paths, command output, XMP payloads, or credentials. Retry only `retryable` requests after correcting the source or dependency condition.

## Compatibility or artifact incident

If an artifact digest, corpus, or profile is no longer approved, pause new intake for that matrix. Do not alter existing provenance. Revert the default to the last approved matrix, preserve evidence, and create a replacement matrix only after the approval checklist passes.

## Retention

Derivatives are regenerable private objects. Expire them according to the configured policy only after deleting their matching provenance in the same lifecycle operation; an incomplete pair is unavailable, never a cache hit.
