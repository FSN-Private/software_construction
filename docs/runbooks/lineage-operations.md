# Lineage and merge operations

## Observe and triage

The `Platform/Lineage` dashboard shows graph size, comparison count, merge preparations/conflicts/confirmations, rewrite attempts, operation failures, and slow requests. The two lineage alarms send to the platform alert topic after five or more matching events in each of three five-minute periods. API events contain an operation ID, safe reason code, counts, and duration. Never add commit messages, XMP, GPS values, paths, or preview locators to operational logs or alert descriptions.

For an alarm, collect the operation IDs and compare failure codes, graph-size distribution, database health, S3 content authority, and feature 008 rendering status. `stale_head` and `stale_working_state` require a fresh owner review; never replay with guessed heads or remove working rows. A preview outage does not change committed history. Check version-control integrity using the existing [integrity runbook](version-control-integrity.md) before any manual recovery.

## Pause mutations

Set API configuration `Lineage:MutationsEnabled=false` and restart or redeploy the API through the normal deployment process. This returns `history_mutations_disabled` for lineage confirmation and feature 006 history mutations while keeping lineage history, comparison, merge preparation, and review readable. Confirm a read request succeeds and a test mutation is rejected before announcing the pause. Restore `true` only after integrity and dependency checks pass. Pending working changes and existing commits remain in place.

## Recover an interrupted action

Look up the operation ID and idempotency key in the audit trail. Read the target and source refs and the resulting commit before advising the owner. A duplicate merge confirmation with the same key and payload replays the recorded result. If neither audit nor commit exists, ask the owner to refresh both heads and review again with a new key. Never submit an old proposal automatically. A displaced non-default ref can be recovered from the feature 006 reflog during its 90-day eligibility window after a fresh owner sign-in. Hidden reachable commits are viewed with show-hidden and do not expire because of hiding.

## Rollout and rollback

Apply additive migrations `018_lineage_merge_content.sql`, `019_tag_audit_link.sql`, and `020_lineage_branch_reachability.sql` before enabling the new API and web entry. Migration 020 backfills branch reachability from immutable commits and holds each branch ref while it is rebuilt; rehearse its duration at reference scale before rollout. Deploy API before web; older clients continue to use feature 006. Verify a private history read, an exact comparison, a clean merge in a disposable library, exact tag audit linkage, and mutation pause before widening exposure. Browser re-authentication visits Cognito `/logout` with a login redirect, clearing the hosted session before a fresh sign-in; this works with the stack's classic hosted UI and managed login. The API requires a signed `auth_time` no older than five minutes for rewrite and recovery.

AWS documents `/logout` and `/login` for [both branding versions](https://docs.aws.amazon.com/cognito/latest/developerguide/managed-login-endpoints.html); `prompt=login` applies only to [managed login](https://docs.aws.amazon.com/cognito/latest/developerguide/authorization-endpoint.html), so it is not used by this stack.

To roll back, disable mutations, remove the web entry and new API version through normal deployment, and retain migrations 018/019/020 and generated XMP objects. Existing commits may reference those objects; dropping the table or object versions would break authority and reconstruction. The tag audit link and image-commit index are additive; older clients ignore them. Keep refs, working changes, audit, and reflog unchanged. Validate feature 006 reads and integrity before resuming mutations. No data migration or destructive cleanup is part of rollback.

If a history page disagrees with immutable commits, withdraw the lineage API while preserving feature 006, verify graph integrity, and rebuild the affected branch's derived index under a ref row lock. In one database transaction, lock that owner/library/branch row in `vc_refs` with `FOR UPDATE`, then call `vc_rebuild_lineage_reachable(owner, library, branch, head_commit_id)` using its current head. Compare indexed commit IDs to authoritative reachable commit IDs before restoring the lineage API. This does not alter commits, refs, or working changes.

## Validation record

Record operation IDs and outcomes in the feature validation document. A production rollout, alert delivery, and manual screen-reader exercise require the release process and are not implied by local template assertions.
