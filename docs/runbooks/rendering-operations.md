# Rendering operations

## Pause, drain, or cancel

An authenticated owner calls `POST /v1/rendering/operations/pause` before maintenance and `resume` afterward. `drain` pauses new intake while running attempts finish. `cancel` pauses intake and cancels only pending attempts; any already-delivered queue envelope is rejected by its durable claim and acknowledged without changing completed provenance. The recovery worker marks attempts still running after ten minutes retryable with the safe `renderer_timeout` code. Completed derivatives and provenance remain valid.

## Failed or stuck render

Use the opaque request and attempt IDs. Check queue age, worker duration, failure code, retry count, renderer matrix, and storage health. Never request or paste worker paths, command output, XMP payloads, or credentials. Retry only `retryable` requests after correcting the source or dependency condition.

## Compatibility or artifact incident

If an artifact digest, corpus, or profile is no longer approved, pause new intake for that matrix. Do not alter existing provenance. Revert the default to the last approved matrix, preserve evidence, and create a replacement matrix only after the approval checklist passes.

## Retention

Derivatives are regenerable private objects. Expire them according to the configured policy only after deleting their matching provenance in the same lifecycle operation; an incomplete pair is unavailable, never a cache hit.
