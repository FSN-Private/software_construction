# Regional acceptance invocation v1

Protected caller uses exact numeric ARN
`arn:aws:lambda:ap-southeast-1:<account>:function:platform-nonprod-acceptance:<version>`.
Reject aliases/$LATEST, wrong account/region/name, wrong codeSHA or runtime/config.
Client zip must be in the checksummed source-bound release manifest. Fixed
configuration target is the expected public HTTPS execute-api origin, no
credentials/path/query/fragment or caller-supplied arbitrary endpoint.

Metadata-only preflight verifies the version and exact release zip before database
startup and again before direct-script mutation. No workload or owner token is
needed. Invocation verifies metadata again before and after work, including
runtime version drift. New nonprod release verification requires both worker
artifacts and the regional zip; historical bundles remain independently readable.

Request: schemaVersion1; invocationId UUID; sourceRevision40hex; releaseRunId
digits; manifestSHA256 and bundleSHA25664hex; origin; five distinct nonempty
{libraryId,assetId,editId} triples; transient bounded token without CR/LF.
All other data are untrusted; no user-supplied code, command or path.

Response: same schema/invocation/source/release/manifest/bundle/origin bindings,
region=ap-southeast-1, qualified functionARN/version, process.version, passed
boolean and rendering/ingest data on success. Failed results expose safe stage
and optionally strictly selected duration/count metrics, not raw child output.
Rendering metrics retain transport=http2, cacheConcurrency20, fixtureCount5,
five uncached start samples, twenty cache samples and p95 values independently
recomputed within rounding precision, thresholds2000/500. Existing smoke process
success establishes actual30s completion. Ingest IDs/hash use existing output.

Caller rejects FunctionError even if Invoke HTTP200, wrong ExecutedVersion,
missing/oversized/malformed result, wrong bindings, nonfinite/missing/extra samples
or failed thresholds. No automatic retry after uncertain execution. Children
run real static scripts with120s timeout,64KiB outputs and minimal environment;
token in environment only during execution, no inherited AWS credentials.
Client total300s, caller read330s/process340s. Safe proof excludes token and
locators; preserve every admitted sample, do not turn errors into qualification.

Deployment retains passed=true with the proof. Offline promotion verification
requires the proof for releases containing a regional zip, recomputes metrics and
checks exact source/release/manifest/bundle and same-account numeric function ARN.
This records nonprod qualification and does not authorize production promotion.

T007 rollback receipt contract: admission returns rollbackReceiptId. Existing
operator-only audit storage captures the exact prior approved compatibility row,
server-side and under matrix/intake locks. POST operations/rollback/{receiptId}
requires the authenticated configured operator, latest receipt, matching configured
candidate/evidence and exact prior or activated-candidate state. Optional
previousDigest must equal the captured prior digest; caller never supplies approval
metadata. Restore the full tuple/reject candidate/close intake atomically. Prior
running API/renderer task configuration must match this digest before ECS recovery.
Resume after verified old API stability; a stopped prior API remains closed.
Lost admission response attempts abort before service recovery. Small JSON-only
nonprod-acceptance-evidence is retained before activation; existing final
nonprod-deployment-evidence follows confirmed activation. A failed/skipped upload
or activation invokes receipt recovery. Transport ambiguity cannot qualify fixtures;
unconfirmed receipt restore must not trigger incompatible task replacement.
