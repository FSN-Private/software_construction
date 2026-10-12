# T034/T035: Recovery purge completion and independent hold release

**Status**: Proposed; local implementation authorization requested for the concrete additions below
**Date**: 2026-10-12

The approved data model permits media-only purge while retaining media-free PostgreSQL snapshots, graph manifests, hashes, paths, metadata and audit. T034 instead assumes every affected snapshot needs replacement. The current executor therefore rejects every normally generated plan before deletion. This proposal reconciles that task with the approved policy without removing the current preservation guard until the replacement checks pass. It does not authorize metadata erasure.

## Snapshot preservation and old-point exclusion

Keep every captured database dump. Before a dump can be classified as media-free, authenticate its exact version/hash and independently pinned migration digest, compare its complete schema/table/column inventory against the pinned schema, reject large objects and unclassified tables/columns, and validate canonical graph manifests and audit continuity in a private native clone. An arbitrary producer schema or table name is insufficient evidence. Current original/XMP bytes live only in object storage; canonical graph JSON and signatures are retained metadata. If a future pinned release embeds media, fail closed pending separately approved sanitization. Do not remove unrelated final copies.

The independent durable media tombstone remains active after physical deletion. Restore and drill must strongly load and authenticate the complete owner/library purge exclusion set before reading media or opening access, bind its digest into validation, filter only independently approved purged logical references, and preserve bytes still required by an unrelated shared identity. Restore a retained old database snapshot only with the exact tombstone overlay applied transactionally to lifecycle/read/render/export admission. Preserve immutable graph/commit bytes and audit history. Graph validation distinguishes an authenticated deliberately purged reference from missing/corrupt protected content. Historical or partial points never produce healthy protection summaries for purged data. Target mutation or exclusion-set movement invalidates readiness/promotion.

## Durable deletion journal and renewal

Use the existing recovery FenceTable `MEDIA#<scope>` partition and conditional Get/Put operations for immutable job, per-version admission and result records. Record IDs include operation ID, plan digest and canonical exact target digest. The private records bind owner/library, asset/edit, category/hash, lifecycle/fence generation, exact bucket/key/version/hash/length, proposal/plan/approval digests, original recovery UTC and worker digest. They never leave recovery storage and add no signing or primary queue publication.

Write and strongly reconcile the admission before any provider delete. After a delete or lost response, confirm absence using the exact version; access denial, timeout or generic errors do not establish absence. Persist the immutable result only after confirmation. A restart authenticates the owner-signed original plan, journal identities, independently signed checkpoint/receipt closure and current fences, and rechecks provider absence before allowing the purge-only verifier to omit an exact previously admitted target. This exception cannot qualify a restore or backup point. Unknown writes/deletes remain pending; repeated receipts preserve original timestamps.

A separately versioned renewal approval binds the same original plan digest, operation/scope/generation and target set, plus a fresh independent MFA approval ID and expiry of at most 15 minutes. The plan's original one-hour window authorizes first admission; it does not silently authorize later deletion. Renewal explicitly authorizes remaining exact targets after expiry only after current holds, complete owner point set, writer intents and fences are rechecked. A changed inventory requires a new reviewed plan rather than renewal. Never reinterpret a version-1 expired approval as renewed authority.

Keep the media fence permanently. Release only the operation's snapshot-admission fence after all exact recovery/isolated-target payload copies are reconciled, signed primary cleanup and feature-012 cleanup acknowledgments match the operation, and a separate fresh independent MFA release signature binds the final receipts and the exact snapshot fence generation. Conflicts, shared copies and provider locks remain explicit pending outcomes. No routine automated retirement can remove a protected final copy.

## Hold-release custody

Bind each observed hold to its ID, kind, owner/library/logical scope, creation UTC and canonical definition digest; include expiry/source reference and any generation. A reused ID or changed definition cannot inherit a release. Primary row deletion, primary release time and primary authentication remain insufficient authority.

The independent owner key signs an exact release approval with hold definition digest, owner/library/scope, approval ID, issue/expiry UTC and separate MFA identity. The recovery verifier accepts it against authenticated checkpoint history and writes a private signed `POINT#<id>/HOLD_RELEASE#<definition-digest>` proof using its existing VerificationTable attributes/actions/signing key. The proof binds approval digest, hold identity/definition, owner/library, exact checkpoint, original recovery-observed UTC and worker digest. Replays preserve the first observed UTC; no backdated primary/owner timestamp shortens the 90-day obligation. An active or changed hold in later trusted history still blocks deletion. No hold release advances health or authorizes purge/promotion by itself. The proof is never published to the primary queue.

## Additional local IAM definition

The unattached proposal `packages/infrastructure/policy-proposals/recovery-purge-completion-support.v1.json` contains only:

1. Purge-role KMS Decrypt through Sydney DynamoDB for the exact existing recovery Control, Intent and Fence tables and recovery account context.
2. Purge-role exact-version Get/Delete and prefix-bounded version enumeration on the independently configured recovery live target bucket's `sha256/*`, plus KMS Decrypt through Sydney S3 for only that bucket/prefix. This reconciles service-controlled isolated payload copies; shared versions remain protected by authenticated references/intents.

No new signing, secret, primary-account, key-administration, retention bypass, deployment, evidence deletion, broad bucket deletion, database privilege or table write privilege is proposed. Existing FenceTable journal grants are reused; the new private HOLD_RELEASE record contract uses existing verifier grants, with no additional action/resource/attribute.

## Validation and rollback

Local tests must cover forged/unclassified/payload-bearing dumps, retained metadata/audit/graph and unrelated final copies; old-point restoration and shared references; failure/restart after each external side effect; accepted-but-lost writes/deletes; provider denial/locks; altered journals, target versions, timestamps and renewal signatures; primary/export acknowledgment mismatch; exact fence release and concurrent writer admission; removed/indefinite/changed holds and backdated approvals. Run synthesized IAM boundary checks and native PostgreSQL tests. Keep the current deletion guard until these preservation checks pass. Rollback stops purge and retains fences, journals, snapshots, provider locks and copies; it does not recreate erased payload.

Approval is for these local contracts, implementation, CDK statements and tests only. No AWS deployment, live read/write, signing, secret retrieval, message delivery, task launch or deletion is requested. T029 promotion and actual owner/account/workload qualification remain separate unfinished gates.
