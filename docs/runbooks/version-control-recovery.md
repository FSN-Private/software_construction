# Version-control divergence, rewrite, and recovery

Owner: library owner; platform operator assists with corruption only.

- `stale_head`: refresh compare/divergence, preserve the caller's working changes, and retry from the new expected head or create a branch. Never select last writer automatically.
- Dirty checkout: use `block` by default, `preserve` to create a stash, or `discard` only with explicit confirmation. Audit the outcome.
- Protected `main`: rewrite, reset, force-move, rename, and delete are forbidden. Treat repeated violations as a security signal; do not bypass the protection row.
- Non-default rewrite: fetch the impact preview, review every displaced ID, then submit its exact confirmation token. The move creates an immutable reflog and a protected recovery ref for at least 90 days.
- Recovery: before the inclusive day-90 deadline, create a named branch from the reflog ID. Verify the recovered commit digest and parentage against the preview. A hold or another ref keeps content after the deadline.
- Expiry: run the owner-scoped expiry command after the deadline. It removes only the generated recovery ref and releases a hold only when no ref or hidden-retention rule still requires the head. Never delete commit rows as part of expiry.

Abort and escalate when the preview changes, a recovery digest differs, integrity verification fails, or a required lifecycle hold is absent.
