# Local failed-cycle publication proposal

Status: awaiting specific authorization after automatic approval review rejected the proposed runtime publication path. No failed-cycle publisher is attached or executed.

The existing `RecoveryCycleEventV1` and `RecoveryCycleEnvelopeV1` contracts in `packages/recovery/RecoveryCatalogContracts.cs` define the exact message. The payload contains schema version, event ID, optional capture/point IDs, owner/library IDs, UTC observation time, stage, optional failed data class, outcome, safe code and recovery-worker build digest. Its envelope contains schema version, `recovery_cycle` message type, worker/signing-key identifiers, public-key fingerprint, payload digest, canonical payload and signature.

The proposed verifier path permits stages `verification` or `publication`, outcomes `incomplete` or `failed`, and failed class `recovery_records`. Safe codes must come from a fixed application allowlist; arbitrary exception text cannot become a code. It excludes media, paths, object locators, credentials and raw errors.

The local implementation would sign with the existing evidence key, persist the exact signed bytes at `POINT#<id>/CYCLE` in the separate VerificationTable, and retry those bytes through only the configured primary recovery-observation queue whose URL and ARN match the configured account, region and name. This proposal does not authorize new IAM permissions, AWS deployment, actual message delivery, or deletion. The separate verifier-role/table proposal remains independently gated.

Required local tests cover the fixed field allowlist, safe-code rejection, exact queue binding, signed-byte retries, interrupted delivery, and absence of sensitive error/path fields. Failure publication must never create a healthy catalog or override independent purge authority.

Automatic approval review stated: “This adds a new durable outbox and runtime SQS publication path for recovery-cycle events, an internal-data egress payload distinct from the specifically approved summary publication; the available authorization does not cover this exact message type and side effect.”
