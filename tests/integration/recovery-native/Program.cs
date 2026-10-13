using System.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Platform.Contracts.VersionControl;
using Platform.Ingest.Worker;
using Platform.Recovery;
using Platform.Recovery.Worker;
using Platform.Storage;

if (args is ["--write-certificates", var certificateDirectory])
{
    WriteCertificates(certificateDirectory);
    return;
}
if (args.Length != 0) throw new ArgumentException("Unsupported native recovery test arguments.");

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
var ct = timeout.Token;
var password = Environment.GetEnvironmentVariable("NATIVE_RECOVERY_PASSWORD")
    ?? throw new InvalidOperationException("Missing ephemeral fixture password.");
var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = "recovery-postgres",
    Port = 5432,
    Database = "recovery_source",
    Username = "postgres",
    Password = password,
    SslMode = SslMode.VerifyFull,
    RootCertificate = "/fixtures/ca.pem",
    Pooling = false,
    Timeout = 15,
    CommandTimeout = 30
}.ConnectionString;
await using var source = new NpgsqlConnection(connectionString);
await source.OpenAsync(ct);
Check((await Scalar(source, "SELECT ssl FROM pg_stat_ssl WHERE pid=pg_backend_pid()", ct)) is true, "TLS was not negotiated.");
Check(Convert.ToInt32(await Scalar(source, "SHOW server_version_num", ct)) / 10000 == 18, "PostgreSQL major is not 18.");
await using (var migrationSource = NpgsqlDataSource.Create(connectionString))
    await DatabaseMigrator.ApplyAsync(migrationSource, ct);
var migrationDigest = await RecoveryWorkerConfiguration.ComputeMigrationDigestAsync("/migrations", ct);
await using (var rehearsal = await source.BeginTransactionAsync(ct))
{
    await Execute(source, "CREATE SCHEMA lifecycle_timestamp_rehearsal; SET LOCAL search_path=lifecycle_timestamp_rehearsal", ct);
    await Execute(source, "CREATE TABLE lifecycle_holds(id uuid PRIMARY KEY); INSERT INTO lifecycle_holds VALUES(gen_random_uuid())", ct);
    var additive = await File.ReadAllTextAsync("/migrations/025_lifecycle_hold_creation.sql", ct);
    await Execute(source, additive, ct);
    await Execute(source, additive, ct);
    Check(await Scalar(source, "SELECT created_at_utc IS NULL FROM lifecycle_holds", ct) is true,
        "Additive timestamp migration invented historical creation UTC.");
    await Execute(source, "INSERT INTO lifecycle_holds(id) VALUES(gen_random_uuid())", ct);
    Check(Convert.ToInt64(await Scalar(source, "SELECT count(*) FROM lifecycle_holds WHERE created_at_utc IS NOT NULL", ct)) == 1,
        "New lifecycle hold did not receive actual creation UTC.");
    await rehearsal.RollbackAsync(ct);
}

const string owner = "native-owner";
const string library = "native-library";
var asset = Guid.NewGuid();
var sha = new string('1', 64);
var foreignAsset = Guid.NewGuid();
var nativeHoldId = Guid.NewGuid();
var commitAt = DateTimeOffset.UtcNow;
commitAt = commitAt.AddTicks(-(commitAt.Ticks % 10));
var parentManifest = new VersionControlManifest(library, [], owner, commitAt, commitAt, "parent",
    [new VersionControlEntry(asset, null, "added", null, sha, "before.dng", "active")]);
var parent = VersionControlManifestCodec.Digest(parentManifest);
var childManifest = new VersionControlManifest(library, [parent], owner, commitAt, commitAt, "child",
    [new VersionControlEntry(asset, null, "modified", sha, sha, "before.dng", "active")]);
var child = VersionControlManifestCodec.Digest(childManifest);
var foreignManifest = new VersionControlManifest(library, [], "foreign-owner", commitAt, commitAt, "foreign",
    [new VersionControlEntry(foreignAsset, null, "added", null, sha, "foreign.dng", "active")]);
var foreign = VersionControlManifestCodec.Digest(foreignManifest);
using var auditRsa = RSA.Create(2048);
const string auditKeyId = "native-independent-audit-key";
const string recoveryAccountId = "444455556666";
var auditSpki = auditRsa.ExportSubjectPublicKeyInfo();
var auditFingerprint = RecoveryEvidenceCodec.Hash(auditSpki);
var auditTrust = new RecoverySigningTrust(auditKeyId, Convert.ToBase64String(auditSpki), auditFingerprint);
var genesisPayload = new RecoveryAuditGenesisPayloadV1(1, 0, new string('0', 64), commitAt.AddSeconds(-1));
var genesisPayloadBytes = RecoveryAuditLedgerCodec.SerializeGenesisPayload(genesisPayload);
var genesisEnvelope = RecoveryAuditLedgerCodec.CreateGenesisEnvelope(genesisPayload, "native-fixture",
    new RecoveryEvidenceSignature(auditKeyId, auditFingerprint,
        auditRsa.SignHash(SHA256.HashData(genesisPayloadBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
var genesisBytes = RecoveryAuditLedgerCodec.SerializeGenesisArtifact(genesisEnvelope);
var genesis = RecoveryAuditLedgerCodec.VerifyGenesisArtifact(genesisBytes, auditTrust, DateTimeOffset.UtcNow);
var edit = Guid.NewGuid();
var generated = new string('5', 64);
var lineageBase = new string('6', 64);
var lineageTarget = new string('7', 64);
var lineageSource = new string('8', 64);
await using (var seed = await source.BeginTransactionAsync(ct))
{
    await using var command = IngestDb.Command(source, """
        INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
        VALUES($1,1,'original','sha256/'||$1,'fixture-version-1','authoritative');
        INSERT INTO assets(id,owner_id,original_sha256,state,format)
        VALUES($2,$3,$1,'active','dng'),($4,'foreign-owner',$1,'active','dng');
        INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active)
        VALUES(gen_random_uuid(),$2,$3,'before.dng','BEFORE.DNG',true);
        """, sha, asset, owner, foreignAsset);
    command.Transaction = seed;
    await command.ExecuteNonQueryAsync(ct);
    foreach (var (commitOwner, commit) in new[] { (owner, parentManifest), (owner, childManifest), ("foreign-owner", foreignManifest) })
    {
        var commitId = VersionControlManifestCodec.Digest(commit);
        await using var insertCommit = IngestDb.Command(source, """
            INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)
            """, commitId, commitOwner, commit.LibraryId, commit.AuthorId, commit.AuthoredAt,
            commit.ReceivedAt, commit.Message, commit.ContractVersion, commit.PolicyVersion, VersionControlManifestCodec.Write(commit));
        insertCommit.Transaction = seed;
        await insertCommit.ExecuteNonQueryAsync(ct);
        var entry = commit.Entries.Single();
        await using var insertEntry = IngestDb.Command(source, """
            INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,edit_id,change_kind,base_content_sha256,content_sha256,display_path,lifecycle_state)
            VALUES($1,0,$2,$3,$4,$5,$6,$7,$8)
            """, commitId, entry.AssetId, entry.EditId, entry.Change, entry.BaseContentSha256,
            entry.ContentSha256, entry.Path, entry.Lifecycle);
        insertEntry.Transaction = seed;
        await insertEntry.ExecuteNonQueryAsync(ct);
    }
    await using var insertParent = IngestDb.Command(source,
        "INSERT INTO vc_commit_parents(commit_id,ordinal,parent_id) VALUES($1,0,$2)", child, parent);
    insertParent.Transaction = seed;
    await insertParent.ExecuteNonQueryAsync(ct);
    await using var metadata = IngestDb.Command(source, """
        INSERT INTO content_objects(sha256,byte_length,category,storage_key,storage_version,integrity_state)
        SELECT digest,1,'xmp','sha256/'||digest,'fixture-version-'||digest,'authoritative'
        FROM unnest(ARRAY[$1::text,$2::text,$3::text,$4::text]) digest;
        INSERT INTO edit_identities(id,asset_id,duplicate_ordinal,xmp_sha256,association_provenance)
        VALUES($5,$6,0,$1,'native-lineage');
        INSERT INTO gallery_preferences(owner_id,hide_geolocation) VALUES($7,false);
        INSERT INTO lineage_merge_content(owner_id,library_id,asset_id,edit_id,sha256,base_sha256,target_sha256,source_sha256,
            target_commit_id,source_commit_id,policy_version,operation_id)
        VALUES($7,$8,$6,$5,$1,$2,$3,$4,$9,$10,'xmp-merge-v1','native-lineage');
        """, generated, lineageBase, lineageTarget, lineageSource, edit, asset, owner, library, child, parent);
    metadata.Transaction = seed;
    await metadata.ExecuteNonQueryAsync(ct);
    var previousEventSha = new string('0', 64);
    long auditSequence = 0;
    foreach (var (eventOwner, eventAsset) in new[] { (owner, asset), ("foreign-owner", foreignAsset) })
    {
        var auditEvent = new RecoveryAuditEventPayloadV1(1, "native-audit-" + eventOwner, ++auditSequence,
            Guid.NewGuid(), eventOwner, library, "fixture_created", "success", eventAsset.ToString("D"),
            commitAt, previousEventSha);
        var eventBytes = RecoveryAuditLedgerCodec.SerializeEvent(auditEvent);
        var eventSha = RecoveryEvidenceCodec.Hash(eventBytes);
        await using var insertAudit = IngestDb.Command(source, """
            INSERT INTO recovery_audit_events(sequence,event_id,owner_id,library_id,event_type,outcome,safe_resource_id,
                occurred_at_utc,previous_sha256,canonical_event,event_sha256,signing_key_id,signature)
            VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
            """, auditSequence, auditEvent.EventId, eventOwner, library, auditEvent.EventType, auditEvent.Outcome,
            auditEvent.SafeResourceId, commitAt, previousEventSha, eventBytes, eventSha, auditKeyId,
            auditRsa.SignHash(SHA256.HashData(eventBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        insertAudit.Transaction = seed;
        await insertAudit.ExecuteNonQueryAsync(ct);
        previousEventSha = eventSha;
    }
    var checkpointPayload = new RecoveryAuditLedgerCheckpointPayloadV1(1, Guid.NewGuid(), auditSequence,
        previousEventSha, auditKeyId, recoveryAccountId, commitAt, genesis.ArtifactSha256);
    var checkpointBytes = RecoveryAuditLedgerCodec.SerializeLedgerCheckpoint(checkpointPayload);
    await using var checkpoint = IngestDb.Command(source, """
        INSERT INTO recovery_audit_checkpoints(checkpoint_id,sequence,event_sha256,signer_key_id,signature,
            recovery_account,created_at_utc,offline_pin_id) VALUES($1,$2,$3,$4,$5,$6,$7,$8);
        UPDATE recovery_audit_state SET last_sequence=$2,last_event_sha256=$3 WHERE singleton=true;
        """, checkpointPayload.CheckpointId, auditSequence, previousEventSha, auditKeyId,
        auditRsa.SignHash(SHA256.HashData(checkpointBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss),
        recoveryAccountId, commitAt, genesis.ArtifactSha256);
    checkpoint.Transaction = seed;
    await checkpoint.ExecuteNonQueryAsync(ct);
    await IngestDb.RecordRecoveryChangeAsync(source, seed, owner, library, "native-seed", "fixture_seed",
        "asset", asset.ToString(), [sha, generated, lineageBase, lineageTarget, lineageSource], DateTimeOffset.UtcNow, ct);
    await using (var nativeHold = IngestDb.Command(source, """
        INSERT INTO recovery_holds(hold_id,owner_id,library_id,asset_id,reason_code,source_reference)
        VALUES($1,$2,$3,$4,'native_fixture','private-native-operation')
        """, nativeHoldId, owner, library, asset))
    {
        nativeHold.Transaction = seed;
        await nativeHold.ExecuteNonQueryAsync(ct);
    }
    await seed.CommitAsync(ct);
}

await using var snapshot = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", source, snapshot))
    await readOnly.ExecuteNonQueryAsync(ct);
var exported = await CaptureRunner.ExportSnapshotAsync(source, snapshot, ct);
var watermark = await CaptureRunner.ReadAndVerifyWatermarkAsync(source, snapshot, owner, library, exported.CutoffUtc, ct);
Check(watermark.LastSequence == 1, "Initial scoped watermark was not captured.");
var objects = await CaptureRunner.ReadObjectClosureAsync(source, snapshot, "native-fixture-bucket", owner, library,
    new HashSet<RecoveryPurgedReference>(), ct);
Check(objects.Count == 5 && objects.Single(item => item.Sha256 == sha).SourceVersionId == "fixture-version-1",
    "Capture did not retain the exact source object closure.");
Check(new[] { generated, lineageBase, lineageTarget, lineageSource }.All(digest =>
    objects.Any(item => item.Sha256 == digest && item.DataClass == RecoveryDataClass.XmpSidecar)),
    "Lineage provenance objects were omitted from the capture closure.");
var counts = await CaptureRunner.ReadClassCountsAsync(source, snapshot, owner, library, objects, ct);
Check(counts[RecoveryDataClass.LibraryMetadata].ItemCount >= 4 && counts[RecoveryDataClass.VersionGraph].ItemCount >= 5,
    "Signed class coverage omitted asset/edit/path/gallery metadata or lineage evidence.");
var classifiedTables = CaptureRunner.ProtectedRelationalTableClasses.SelectMany(tables => tables).ToArray();
Check(classifiedTables.Distinct(StringComparer.Ordinal).Count() == classifiedTables.Length,
    "A protected relational table is counted in multiple data classes.");
var declaredTables = classifiedTables.Append("content_objects").ToHashSet(StringComparer.Ordinal);
await using (var schema = new NpgsqlCommand("SELECT tablename FROM pg_tables WHERE schemaname='public'", source, snapshot))
await using (var reader = await schema.ExecuteReaderAsync(ct))
{
    var actualTables = new HashSet<string>(StringComparer.Ordinal);
    while (await reader.ReadAsync(ct)) actualTables.Add(reader.GetString(0));
    Check(actualTables.SetEquals(declaredTables),
        "Current database schema and protected-table classification differ; classify new tables before capture.");
}

await using (var writer = new NpgsqlConnection(connectionString))
{
    await writer.OpenAsync(ct);
    await using var mutation = await writer.BeginTransactionAsync(ct);
    await using var rename = new NpgsqlCommand("UPDATE asset_paths SET display_path='after.dng',collision_key='AFTER.DNG' WHERE asset_id=$1", writer, mutation);
    rename.Parameters.AddWithValue(asset);
    await rename.ExecuteNonQueryAsync(ct);
    await IngestDb.RecordRecoveryChangeAsync(writer, mutation, owner, library, "native-concurrent", "fixture_rename",
        "asset", asset.ToString(), [sha], DateTimeOffset.UtcNow, ct);
    await mutation.CommitAsync(ct);
}
var unchanged = await CaptureRunner.ReadAndVerifyWatermarkAsync(source, snapshot, owner, library, exported.CutoffUtc, ct);
Check(unchanged.LastSequence == 1, "Concurrent commit entered the exported MVCC snapshot.");

var configuration = new RecoveryWorkerConfiguration("capture");
var rootPem = await File.ReadAllTextAsync("/fixtures/ca.pem", ct);
var rootDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rootPem))).ToLowerInvariant();
Environment.SetEnvironmentVariable("Recovery__DatabaseRootCertificate", rootPem);
Environment.SetEnvironmentVariable("Recovery__DatabaseRootCertificateSha256", rootDigest);
var pinnedRoot = CaptureRunner.WriteDatabaseRootCertificate(configuration, "/tmp");
Environment.SetEnvironmentVariable("Recovery__DatabaseRootCertificateSha256", new string('0', 64));
await RejectCode(() =>
{
    _ = CaptureRunner.WriteDatabaseRootCertificate(configuration, "/tmp");
    return Task.CompletedTask;
}, "database_root_certificate_digest_mismatch");
Environment.SetEnvironmentVariable("Recovery__DatabaseRootCertificateSha256", rootDigest);
var credentials = new CaptureRunner.DatabaseConnection(connectionString, password, "recovery-postgres", 5432,
    "recovery_source", "postgres", pinnedRoot);
const string dump = "/tmp/native-recovery.dump";
await CaptureRunner.CreateDatabaseDumpAsync(configuration, credentials, exported.SnapshotId, dump, ct);
await RejectCode(() => CaptureRunner.CreateDatabaseDumpAsync(configuration,
    credentials with { RootCertificatePath = "/fixtures/untrusted-ca.pem" }, exported.SnapshotId, "/tmp/invalid.dump", ct), "postgres_tool_failed");
await snapshot.CommitAsync(ct);
const string nativeHoldRows = "/tmp/native-recovery-holds.sql";
await RecoveryPurgeRunner.RunDumpExtractAsync("pg_restore", "recovery_holds", dump, nativeHoldRows, ct);
var capturedNativeHoldRow = RecoveryPurgeRunner.ReadCopyRows(nativeHoldRows).Single(row =>
    row.GetValueOrDefault("hold_id") == nativeHoldId.ToString("D"));
var capturedNativeHold = RecoveryPurgeRunner.ReadHoldObservation(capturedNativeHoldRow, "recovery", exported.CutoffUtc);
Check(capturedNativeHold.HoldId == nativeHoldId && capturedNativeHold.ActiveAtSnapshot,
    "Actual PostgreSQL hold schema/COPY extraction lost the independent hold identity.");
var primaryReleasedNativeHold = new Dictionary<string, string?>(capturedNativeHoldRow)
    { ["released_at_utc"] = exported.CutoffUtc.ToString("O") };
Check(RecoveryPurgeRunner.ReadHoldObservation(primaryReleasedNativeHold, "recovery", exported.CutoffUtc) == capturedNativeHold,
    "Primary release timestamp changed independently captured protection.");
Check(Convert.ToInt64(await Scalar(source, "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id='native-owner'", ct)) == 2,
    "Concurrent source mutation did not commit.");

var manifest = new RecoveryPointManifestV1(1, Guid.NewGuid(), Guid.NewGuid(), owner, library, "ap-southeast-1",
    DateTimeOffset.UtcNow, exported.CutoffUtc, exported.SnapshotId, exported.Lsn, migrationDigest, watermark,
    [], objects, counts.Select(pair => new RecoveryClassCoverage(pair.Key, pair.Value.ItemCount, pair.Value.ByteLength)).ToArray());
var recovery = new RecoveryDrillRunner.RecoveryDatabaseConnection("recovery-postgres", 5432, "postgres", password,
    "/fixtures/ca.pem", connectionString);
const string targetName = "recovery_drill_native";
Task ValidateRecovered() => RecoveryDrillRunner.ValidateRestoredPointAsync(recovery, targetName, manifest,
    auditTrust, genesis, migrationDigest, "/migrations", recoveryAccountId, ct);
await RecoveryDrillRunner.CreateDatabaseAsync(recovery, targetName, ct);
try
{
    var trustedInventorySha256 = await RecoveryTrustedDumpRestore.RestoreAsync(recovery,targetName,dump,"/migrations",migrationDigest,"pg_dump","pg_restore",ct);
    var targetString = new NpgsqlConnectionStringBuilder(connectionString) { Database = targetName }.ConnectionString;
    await using var target = new NpgsqlConnection(targetString);
    await target.OpenAsync(ct);
    await using (var classification = await target.BeginTransactionAsync(ct))
    {
        Check(await RecoverySnapshotPreservation.RequireMediaFreeAsync(target, classification, "/migrations",
            trustedInventorySha256, ct) == trustedInventorySha256, "Pinned native snapshot inventory was not preserved.");
        const string changedMigrationDirectory = "/tmp/unclassified-media-migrations";
        Directory.CreateDirectory(changedMigrationDirectory);
        await File.WriteAllTextAsync(Path.Combine(changedMigrationDirectory, "001_changed.sql"),
            "CREATE TABLE embedded_media(payload bytea);", ct);
        await RejectCode(() => RecoverySnapshotPreservation.RequireMediaFreeAsync(target, classification,
            changedMigrationDirectory, trustedInventorySha256, ct), "snapshot_media_classification_required");
        await using var largeObject = new NpgsqlCommand("SELECT lo_create(0)", target, classification);
        await largeObject.ExecuteScalarAsync(ct);
        await RejectCode(() => RecoverySnapshotPreservation.RequireMediaFreeAsync(target, classification, "/migrations",
            trustedInventorySha256, ct), "snapshot_large_objects_not_classified");
        await classification.RollbackAsync(ct);
    }
    await using (var classification = await target.BeginTransactionAsync(ct))
    {
        await using var added = new NpgsqlCommand("ALTER TABLE assets ADD COLUMN unclassified_media bytea", target, classification);
        await added.ExecuteNonQueryAsync(ct);
        await RejectCode(() => RecoverySnapshotPreservation.RequireMediaFreeAsync(target, classification, "/migrations",
            trustedInventorySha256, ct), "snapshot_schema_inventory_changed");
        await classification.RollbackAsync(ct);
    }
    await using (var classification = await target.BeginTransactionAsync(ct))
    {
        // The pinned schema itself permits arbitrary binary evidence. Such bytes
        // cannot receive media-free authority just because coverage includes them.
        await using var injected = new NpgsqlCommand("""
            INSERT INTO recovery_points(point_id,capture_id,owner_id,library_id,source_region,captured_at_utc,
                snapshot_id,snapshot_lsn,snapshot_cutoff_utc,change_sequence,migration_digest,manifest_schema_version,
                manifest_sha256,manifest_bytes,manifest,state)
            VALUES(gen_random_uuid(),gen_random_uuid(),'native-owner','native-library','ap-southeast-1',now(),
                'fixture','0/0',now(),0,repeat('a',64),1,repeat('b',64),decode('ffd8ffe000104a464946','hex'),'{}','capturing')
            """, target, classification);
        await injected.ExecuteNonQueryAsync(ct);
        await RejectCode(() => RecoverySnapshotPreservation.RequireMediaFreeAsync(target, classification, "/migrations",
            trustedInventorySha256, ct), "snapshot_binary_evidence_not_classified");
        await classification.RollbackAsync(ct);
    }
    Check((string?)await Scalar(target, "SELECT display_path FROM asset_paths WHERE owner_id='native-owner'", ct) == "before.dng",
        "Native dump included a mutation committed after its exported snapshot.");
    Check(Convert.ToInt64(await Scalar(target, "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id='native-owner'", ct)) == 1,
        "Dump and inventory watermarks differ.");
    await ValidateRecovered();
    await using (var classification = await target.BeginTransactionAsync(ct))
    {
        await using var triggers = new NpgsqlCommand("ALTER TABLE vc_commits DISABLE TRIGGER ALL", target, classification);
        await triggers.ExecuteNonQueryAsync(ct);
        await using var corruptForeign = new NpgsqlCommand("UPDATE vc_commits SET manifest_bytes=decode('ffd8ffe000104a464946','hex') WHERE id=$1", target, classification);
        corruptForeign.Parameters.AddWithValue(foreign);
        await corruptForeign.ExecuteNonQueryAsync(ct);
        await RejectCode(() => RecoverySnapshotPreservation.RequireMediaFreeAsync(target, classification, "/migrations",
            trustedInventorySha256, ct), "restored_version_graph_invalid");
        await classification.RollbackAsync(ct);
    }

    var originalEventSignature = (byte[])(await Scalar(target, "SELECT signature FROM recovery_audit_events WHERE sequence=1", ct))!;
    var originalCheckpointSignature = (byte[])(await Scalar(target, "SELECT signature FROM recovery_audit_checkpoints WHERE sequence=2", ct))!;
    var originalHead = (string)(await Scalar(target, "SELECT last_event_sha256 FROM recovery_audit_state WHERE singleton=true", ct))!;
    var firstEventHead = (string)(await Scalar(target, "SELECT event_sha256 FROM recovery_audit_events WHERE sequence=1", ct))!;
    var trustedAuditHeads = new RecoveryAuditHeadRequirementV1(new(1, firstEventHead.Trim()), new(2, originalHead.Trim()));
    Task ValidateTrustedAuditHeads() => RecoveryDrillRunner.ValidateRestoredPointWithAuditHeadAsync(recovery,
        targetName, manifest, auditTrust, genesis, migrationDigest, "/migrations", recoveryAccountId,
        trustedAuditHeads, configuration: null, ct);
    await ValidateTrustedAuditHeads();
    var originalMigrationSha = (string)(await Scalar(target, "SELECT sha256 FROM platform_schema_migrations WHERE id='022_backup_recovery.sql'", ct))!;
    await Execute(target, "ALTER TABLE recovery_audit_events DISABLE TRIGGER ALL; ALTER TABLE recovery_audit_checkpoints DISABLE TRIGGER ALL", ct);
    foreach (var (badSql, resetSql, originalValue, safeCode) in new (string, string, object, string)[]
    {
        ("UPDATE recovery_audit_events SET signature=decode('01','hex') WHERE sequence=1",
            "UPDATE recovery_audit_events SET signature=$1 WHERE sequence=1", originalEventSignature, "recovery_audit_signature_invalid"),
        ("UPDATE recovery_audit_checkpoints SET signature=decode('01','hex') WHERE sequence=2",
            "UPDATE recovery_audit_checkpoints SET signature=$1 WHERE sequence=2", originalCheckpointSignature, "recovery_audit_checkpoint_signature_invalid"),
        ("UPDATE recovery_audit_state SET last_event_sha256=repeat('9',64) WHERE singleton=true",
            "UPDATE recovery_audit_state SET last_event_sha256=$1 WHERE singleton=true", originalHead, "recovery_audit_state_head_mismatch"),
        ("UPDATE platform_schema_migrations SET sha256=repeat('9',64) WHERE id='022_backup_recovery.sql'",
            "UPDATE platform_schema_migrations SET sha256=$1 WHERE id='022_backup_recovery.sql'", originalMigrationSha, "restored_migration_ledger_mismatch")
    })
    {
        await Execute(target, badSql, ct);
        await RejectCode(ValidateRecovered, safeCode);
        await using var reset = IngestDb.Command(target, resetSql, originalValue);
        await reset.ExecuteNonQueryAsync(ct);
    }
    // Remove a valid signed tail and replace the database's self-reported head with
    // its valid signed prefix. A separately trusted recovery head must still reject it.
    await Execute(target, """
        CREATE TEMP TABLE saved_audit_tail AS SELECT * FROM recovery_audit_events WHERE sequence=2;
        CREATE TEMP TABLE saved_audit_checkpoint AS SELECT * FROM recovery_audit_checkpoints WHERE sequence=2;
        DELETE FROM recovery_audit_checkpoints WHERE sequence=2;
        DELETE FROM recovery_audit_events WHERE sequence=2;
        UPDATE recovery_audit_state SET last_sequence=1,
            last_event_sha256=(SELECT event_sha256 FROM recovery_audit_events WHERE sequence=1) WHERE singleton=true;
        """, ct);
    await RejectCode(ValidateTrustedAuditHeads, "recovery_audit_previous_head_not_in_chain");
    await Execute(target, """
        INSERT INTO recovery_audit_events SELECT * FROM saved_audit_tail;
        INSERT INTO recovery_audit_checkpoints SELECT * FROM saved_audit_checkpoint;
        UPDATE recovery_audit_state SET last_sequence=2,
            last_event_sha256=(SELECT event_sha256 FROM recovery_audit_events WHERE sequence=2) WHERE singleton=true;
        DROP TABLE saved_audit_tail,saved_audit_checkpoint;
        ALTER TABLE recovery_audit_events ENABLE TRIGGER ALL;
        ALTER TABLE recovery_audit_checkpoints ENABLE TRIGGER ALL;
        """, ct);
    await ValidateTrustedAuditHeads();
    await ValidateRecovered();

    await using (var originalVersion = IngestDb.Command(target, "SELECT storage_version FROM content_objects WHERE sha256=$1", sha))
    {
        var storedVersion = (string)(await originalVersion.ExecuteScalarAsync(ct))!;
        await using var corruptVersion = IngestDb.Command(target,
            "UPDATE content_objects SET storage_version=$1 WHERE sha256=$2", "tampered-version", sha);
        await corruptVersion.ExecuteNonQueryAsync(ct);
        await RejectCode(ValidateRecovered, "restored_object_closure_mismatch");
        await using var resetVersion = IngestDb.Command(target,
            "UPDATE content_objects SET storage_version=$1 WHERE sha256=$2", storedVersion, sha);
        await resetVersion.ExecuteNonQueryAsync(ct);
    }
    await ValidateRecovered();

    foreach (var (table, column, badValue) in new[]
    {
        ("vc_commits", "message", "xxxxx"),
        ("vc_commit_entries", "display_path", "tamper.dng")
    })
    {
        var identityColumn = table == "vc_commits" ? "id" : "commit_id";
        await using var readOriginal = IngestDb.Command(target,
            $"SELECT {column} FROM {table} WHERE {identityColumn}=$1", child);
        var originalValue = (string)(await readOriginal.ExecuteScalarAsync(ct))!;
        await Execute(target, $"ALTER TABLE {table} DISABLE TRIGGER ALL", ct);
        await using var corrupt = IngestDb.Command(target,
            $"UPDATE {table} SET {column}=$1 WHERE {identityColumn}=$2", badValue, child);
        await corrupt.ExecuteNonQueryAsync(ct);
        await RejectCode(ValidateRecovered, "restored_version_graph_invalid");
        await using var reset = IngestDb.Command(target,
            $"UPDATE {table} SET {column}=$1 WHERE {identityColumn}=$2", originalValue, child);
        await reset.ExecuteNonQueryAsync(ct);
        await Execute(target, $"ALTER TABLE {table} ENABLE TRIGGER ALL", ct);
    }
    await ValidateRecovered();

    await CandidateChecks(target, manifest, auditRsa, auditTrust, genesis, trustedAuditHeads, migrationDigest, recoveryAccountId, ct);
    await RetainedRestoreChecks(target, manifest, auditRsa, auditTrust, ct);

    await using (var corruption = new NpgsqlCommand("UPDATE vc_commit_parents SET parent_id=$1 WHERE commit_id=$2", target))
    {
        // Simulate a damaged restored artifact; production immutable triggers stay enabled.
        await Execute(target, "ALTER TABLE vc_commit_parents DISABLE TRIGGER ALL", ct);
        corruption.Parameters.AddWithValue(foreign);
        corruption.Parameters.AddWithValue(child);
        await corruption.ExecuteNonQueryAsync(ct);
    }
    await RejectCode(ValidateRecovered, "restored_version_graph_invalid");
    await Execute(target, "DROP TABLE assets CASCADE", ct);
    await RejectCode(ValidateRecovered, "restored_database_schema_incomplete");
}
finally
{
    await RecoveryDrillRunner.DropDatabaseAsync(recovery, targetName, ct);
}

// Real migration-era snapshots preserve one legacy hold definition while using
// separate exact schema/migration authorities for each release.
var legacyDirectory = "/tmp/legacy-lifecycle-migrations";
Directory.CreateDirectory(legacyDirectory);
foreach (var file in Directory.GetFiles("/migrations", "*.sql"))
    if (Path.GetFileName(file) != "025_lifecycle_hold_creation.sql")
        File.Copy(file, Path.Combine(legacyDirectory, Path.GetFileName(file)), overwrite: true);
var legacyMigrationDigest = await RecoveryWorkerConfiguration.ComputeMigrationDigestAsync(legacyDirectory, ct);
var legacyLifecycleId = Guid.NewGuid();
await Execute(source, "ALTER TABLE lifecycle_holds DROP COLUMN created_at_utc; DELETE FROM platform_schema_migrations WHERE id='025_lifecycle_hold_creation.sql'", ct);
await using (var legacyHold = IngestDb.Command(source,
    "INSERT INTO lifecycle_holds(id,asset_id,reason,source_reference) VALUES($1,$2,'restore','native-migration-era')", legacyLifecycleId, asset))
    await legacyHold.ExecuteNonQueryAsync(ct);
var lifecycleObservations = new List<RecoveryRetentionPointObservationV1>();
string? legacyDefinition = null;
try
{
    for (var stage = 0; stage < 2; stage++)
    {
        if (stage == 1)
        {
            var script = await File.ReadAllTextAsync("/migrations/025_lifecycle_hold_creation.sql", ct);
            await Execute(source, script, ct);
            await using var ledger = IngestDb.Command(source,
                "INSERT INTO platform_schema_migrations(id,sha256) VALUES('025_lifecycle_hold_creation.sql',$1)",
                RecoveryEvidenceCodec.Hash(System.Text.Encoding.UTF8.GetBytes(script)));
            await ledger.ExecuteNonQueryAsync(ct);
        }
        var schemaDirectory = stage == 0 ? legacyDirectory : "/migrations";
        var schemaDigest = stage == 0 ? legacyMigrationDigest : migrationDigest;
        await using var lifecycleSnapshot = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var lifecycleExport = await CaptureRunner.ExportSnapshotAsync(source, lifecycleSnapshot, ct);
        var lifecycleWatermark = await CaptureRunner.ReadAndVerifyWatermarkAsync(source, lifecycleSnapshot,
            owner, library, lifecycleExport.CutoffUtc, ct);
        var lifecycleCounts = await CaptureRunner.ReadClassCountsAsync(source, lifecycleSnapshot, owner, library, objects, ct);
        var lifecycleManifest = manifest with { RecoveryPointId = Guid.NewGuid(), CaptureId = Guid.NewGuid(),
            CapturedAtUtc = DateTimeOffset.UtcNow, SnapshotCutoffUtc = lifecycleExport.CutoffUtc,
            SnapshotId = lifecycleExport.SnapshotId, SnapshotLsn = lifecycleExport.Lsn,
            MigrationDigest = schemaDigest, ChangeWatermark = lifecycleWatermark,
            Coverage = lifecycleCounts.Select(pair => new RecoveryClassCoverage(pair.Key, pair.Value.ItemCount, pair.Value.ByteLength)).ToArray() };
        var lifecycleDump = "/tmp/lifecycle-history-" + stage + ".dump";
        await CaptureRunner.CreateDatabaseDumpAsync(configuration, credentials, lifecycleExport.SnapshotId, lifecycleDump, ct);
        await lifecycleSnapshot.CommitAsync(ct);
        var cloneName = "recovery_verify_lifecycle_" + stage;
        await RecoveryDrillRunner.CreateDatabaseAsync(recovery, cloneName, ct);
        try
        {
            await RecoveryTrustedDumpRestore.RestoreAsync(recovery, cloneName, lifecycleDump,
                schemaDirectory, schemaDigest, "pg_dump", "pg_restore", ct);
            await RecoveryDrillRunner.ValidateRestoredPointAsync(recovery, cloneName, lifecycleManifest,
                auditTrust, genesis, schemaDigest, schemaDirectory, recoveryAccountId, ct);
            var holdsFile = "/tmp/lifecycle-history-holds-" + stage + ".sql";
            await RecoveryPurgeRunner.RunDumpExtractAsync("pg_restore", "lifecycle_holds", lifecycleDump, holdsFile, ct);
            var row = RecoveryPurgeRunner.ReadCopyRows(holdsFile).Single(item => item.GetValueOrDefault("id") == legacyLifecycleId.ToString("D"));
            var hold = RecoveryPurgeRunner.ReadHoldObservation(row, "lifecycle", lifecycleExport.CutoffUtc);
            Check(hold.CreatedAtUtc is null, "Migration-era legacy hold gained an invented creation timestamp.");
            legacyDefinition ??= hold.ScopeSha256;
            Check(hold.ScopeSha256 == legacyDefinition, "Migration-era capture rebound the legacy hold definition.");
            lifecycleObservations.Add(new(lifecycleManifest.RecoveryPointId, lifecycleExport.CutoffUtc,
                DateTimeOffset.UtcNow, sha, sha, auditTrust.KeyId, null, 0, 0, null, null, [hold]));
        }
        finally { await RecoveryDrillRunner.DropDatabaseAsync(recovery, cloneName, ct); }
    }
    var origin = RetentionService.ResolveLifecycleHoldCreation(legacyLifecycleId, legacyDefinition!, lifecycleObservations);
    Check(origin == (lifecycleObservations[0].RecoveryPointId, lifecycleObservations[0].SnapshotCutoffUtc),
        "Mixed-release physical snapshots did not select the original legacy observation.");
}
finally
{
    await using var cleanup = IngestDb.Command(source, "DELETE FROM lifecycle_holds WHERE id=$1", legacyLifecycleId);
    await cleanup.ExecuteNonQueryAsync(ct);
}

// Payload present before capture has honest coverage and a matching exact dump.
// Those checks alone cannot authorize a media-free preservation proof.
var payloadPointId = Guid.NewGuid();
await using (var injected = IngestDb.Command(source, """
    INSERT INTO recovery_points(point_id,capture_id,owner_id,library_id,source_region,captured_at_utc,
        snapshot_id,snapshot_lsn,snapshot_cutoff_utc,change_sequence,migration_digest,manifest_schema_version,
        manifest_sha256,manifest_bytes,manifest,state)
    VALUES($1,$2,$3,$4,'ap-southeast-1',now(),'fixture','0/0',now(),0,
        repeat('a',64),1,repeat('b',64),decode('ffd8ffe000104a464946','hex'),'{}','capturing')
    """, payloadPointId, Guid.NewGuid(), owner, library))
    await injected.ExecuteNonQueryAsync(ct);
const string payloadTarget = "recovery_verify_payload";
try
{
    await using var payloadSnapshot = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
    var payloadExport = await CaptureRunner.ExportSnapshotAsync(source, payloadSnapshot, ct);
    var payloadWatermark = await CaptureRunner.ReadAndVerifyWatermarkAsync(source, payloadSnapshot,
        owner, library, payloadExport.CutoffUtc, ct);
    var payloadCounts = await CaptureRunner.ReadClassCountsAsync(source, payloadSnapshot, owner, library, objects, ct);
    var payloadManifest = manifest with { RecoveryPointId = Guid.NewGuid(), CaptureId = Guid.NewGuid(),
        CapturedAtUtc = DateTimeOffset.UtcNow, SnapshotCutoffUtc = payloadExport.CutoffUtc,
        SnapshotId = payloadExport.SnapshotId, SnapshotLsn = payloadExport.Lsn, ChangeWatermark = payloadWatermark,
        Coverage = payloadCounts.Select(pair => new RecoveryClassCoverage(pair.Key, pair.Value.ItemCount, pair.Value.ByteLength)).ToArray() };
    const string payloadDump = "/tmp/payload-recovery.dump";
    await CaptureRunner.CreateDatabaseDumpAsync(configuration, credentials, payloadExport.SnapshotId, payloadDump, ct);
    await payloadSnapshot.CommitAsync(ct);
    await RecoveryDrillRunner.CreateDatabaseAsync(recovery, payloadTarget, ct);
    try
    {
        var inventory = await RecoveryTrustedDumpRestore.RestoreAsync(recovery, payloadTarget, payloadDump,
            "/migrations", migrationDigest, "pg_dump", "pg_restore", ct);
        await RecoveryDrillRunner.ValidateRestoredPointAsync(recovery, payloadTarget, payloadManifest,
            auditTrust, genesis, migrationDigest, "/migrations", recoveryAccountId, ct);
        await using var payloadClone = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Database = payloadTarget }.ConnectionString);
        await payloadClone.OpenAsync(ct);
        await using var classification = await payloadClone.BeginTransactionAsync(ct);
        await RejectCode(() => RecoverySnapshotPreservation.RequireMediaFreeAsync(payloadClone, classification,
            "/migrations", inventory, ct), "snapshot_binary_evidence_not_classified");
        await classification.RollbackAsync(ct);
    }
    finally { await RecoveryDrillRunner.DropDatabaseAsync(recovery, payloadTarget, ct); }
}
finally
{
    await using var cleanup = IngestDb.Command(source, "DELETE FROM recovery_points WHERE point_id=$1", payloadPointId);
    await cleanup.ExecuteNonQueryAsync(ct);
}

// Captured schema is producer-controlled. A trigger that would execute during
// a conventional restore must be rejected before any captured data is loaded.
const string maliciousTarget = "recovery_verify_malicious";
await Execute(source, """
    CREATE FUNCTION public.producer_restore_trap() RETURNS trigger LANGUAGE plpgsql AS $$
    BEGIN RAISE EXCEPTION 'producer_sql_executed'; END;
    $$;
    CREATE TRIGGER producer_restore_trap BEFORE INSERT ON public.assets
        FOR EACH ROW EXECUTE FUNCTION public.producer_restore_trap();
    """, ct);
try
{
    await using var maliciousSnapshot = await source.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
    var maliciousExport = await CaptureRunner.ExportSnapshotAsync(source, maliciousSnapshot, ct);
    await CaptureRunner.CreateDatabaseDumpAsync(configuration, credentials, maliciousExport.SnapshotId,
        "/tmp/malicious-recovery.dump", ct);
    await maliciousSnapshot.CommitAsync(ct);
    await RecoveryDrillRunner.CreateDatabaseAsync(recovery, maliciousTarget, ct);
    try
    {
        await RejectCode(() => RecoveryTrustedDumpRestore.RestoreAsync(recovery, maliciousTarget,
            "/tmp/malicious-recovery.dump", "/migrations", migrationDigest, "pg_dump", "pg_restore", ct),
            "recovery_dump_schema_untrusted");
        await using var isolated = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString)
            { Database = maliciousTarget }.ConnectionString);
        await isolated.OpenAsync(ct);
        Check(Convert.ToInt64(await Scalar(isolated, "SELECT count(*) FROM assets", ct)) == 0,
            "Rejected archive loaded captured rows.");
        Check(await Scalar(isolated, "SELECT to_regprocedure('public.producer_restore_trap()')::text", ct) is DBNull,
            "Producer function entered trusted schema.");
    }
    finally { await RecoveryDrillRunner.DropDatabaseAsync(recovery, maliciousTarget, ct); }
}
finally
{
    await Execute(source, "DROP TRIGGER producer_restore_trap ON public.assets; DROP FUNCTION public.producer_restore_trap()", ct);
}

await using (var orphanTransaction = await source.BeginTransactionAsync(ct))
{
    await using var orphan = new NpgsqlCommand("""
        INSERT INTO recovery_audit_checkpoints(checkpoint_id,sequence,event_sha256,signer_key_id,signature,recovery_account,created_at_utc,offline_pin_id)
        VALUES(gen_random_uuid(),999,repeat('a',64),'fixture-key',decode('01','hex'),'444455556666',now(),'fixture-anchor')
        """, source, orphanTransaction);
    await orphan.ExecuteNonQueryAsync(ct);
    await RejectCode(async () => { _ = await CaptureRunner.ReadClassCountsAsync(source, orphanTransaction, owner, library, objects, ct); },
        "recovery_audit_checkpoint_orphan");
    await orphanTransaction.RollbackAsync(ct);
}

await using (var missingLineage = await source.BeginTransactionAsync(ct))
{
    await using var corruptLocator = new NpgsqlCommand("UPDATE content_objects SET storage_version=NULL WHERE sha256=$1", source, missingLineage);
    corruptLocator.Parameters.AddWithValue(lineageSource);
    await corruptLocator.ExecuteNonQueryAsync(ct);
    await RejectCode(async () =>
    {
        _ = await CaptureRunner.ReadObjectClosureAsync(source, missingLineage, "native-fixture-bucket", owner, library,
            new HashSet<RecoveryPurgedReference>(), ct);
    }, "referenced_object_incomplete");
    await missingLineage.RollbackAsync(ct);
}

foreach (var mutationSql in new[]
{
    "UPDATE content_objects SET category='original' WHERE sha256=$1",
    "UPDATE content_objects SET integrity_state='quarantined' WHERE sha256=$1",
    "UPDATE content_objects SET integrity_state='integrity-error' WHERE sha256=$1"
})
{
    await using var unavailableLineage = await source.BeginTransactionAsync(ct);
    await using var mutation = new NpgsqlCommand(mutationSql, source, unavailableLineage);
    mutation.Parameters.AddWithValue(lineageSource);
    await mutation.ExecuteNonQueryAsync(ct);
    await RejectCode(async () =>
    {
        _ = await CaptureRunner.ReadObjectClosureAsync(source, unavailableLineage, "native-fixture-bucket", owner, library,
            new HashSet<RecoveryPurgedReference>(), ct);
    }, "referenced_object_unavailable");
    await unavailableLineage.RollbackAsync(ct);
}

await RejectTls(new NpgsqlConnectionStringBuilder(connectionString) { RootCertificate = "/fixtures/untrusted-ca.pem" }.ConnectionString, ct);
await RejectTls(new NpgsqlConnectionStringBuilder(connectionString) { Host = "recovery-postgres-wrong" }.ConnectionString, ct);
Console.WriteLine("Native PostgreSQL18/TLS capture and restore passed; concurrent mutation excluded, lineage/metadata covered, missing/quarantined/misclassified objects and foreign graph/schema/TLS rejected. No cloud qualification established.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct)
{
    await using var command = new NpgsqlCommand(sql, connection);
    await command.ExecuteNonQueryAsync(ct);
}
static async Task<object?> Scalar(NpgsqlConnection connection, string sql, CancellationToken ct)
{
    await using var command = new NpgsqlCommand(sql, connection);
    return await command.ExecuteScalarAsync(ct);
}
static async Task RejectCode(Func<Task> action, string safeCode)
{
    try { await action(); }
    catch (RecoveryWorkerException exception) when (exception.SafeCode == safeCode) { return; }
    throw new InvalidOperationException("Expected native rejection: " + safeCode);
}
static async Task RetainedRestoreChecks(NpgsqlConnection connection, RecoveryPointManifestV1 manifest,
    RSA rsa, RecoverySigningTrust trust, CancellationToken ct)
{
    var hash = new string('a', 64);
    var source = manifest with { Components = [
        new("database", RecoveryDataClass.DatabaseSnapshot, "capture/database.dump", "dump-v1", hash, 1),
        new("inventory", RecoveryDataClass.ObjectInventory, "capture/objects.json", "inventory-v1", hash, 1)] };
    var final = source with { Components = [.. source.Components,
        new("owner-access", RecoveryDataClass.AccessEvidence, "evidence/access.json", "access-v1", hash, 1)] };
    Check(source.Digest() != final.Digest(), "Native retained restore fixture must have distinct source/final manifests.");
    var now = DateTimeOffset.UtcNow;
    var coverage = RecoveryEvidenceCodec.ObservationClasses.Select(dataClass =>
        new RecoveryCatalogCoverageV1(dataClass, 0, 0, 0, 0, "healthy", [])).ToArray();
    var catalog = new RecoveryCatalogPayloadV1(1, manifest.RecoveryPointId, manifest.CaptureId,
        manifest.OwnerId, manifest.LibraryId, "111122223333", "ap-southeast-1", "444455556666", "ap-southeast-2",
        manifest.CapturedAtUtc, manifest.SnapshotCutoffUtc, manifest.SnapshotId, manifest.SnapshotLsn,
        manifest.ChangeWatermark.LastSequence, manifest.ChangeWatermark.LatestChangedAtUtc, manifest.MigrationDigest,
        hash, hash, hash, hash, hash, final.Digest(), hash, now, "native-fixture", coverage);
    var catalogBytes = RecoveryCatalogCodec.SerializePayload(catalog);
    var envelope = RecoveryCatalogCodec.CreateEnvelope(catalog, "native-fixture", new(trust.KeyId, trust.PublicKeySha256,
        rsa.SignHash(SHA256.HashData(catalogBytes), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
    var verified = RecoveryCatalogCodec.Verify(envelope, trust);
    var proposal = RecoveryRestoreProposalV1.Create(Guid.NewGuid(), manifest.OwnerId, manifest.LibraryId,
        manifest.RecoveryPointId, new("full_library", null, null), now.AddHours(1));
    await using var transaction = await connection.BeginTransactionAsync(ct);
    await RejectCode(async () => { _ = await RecoveryRestoreRunner.ReadExistingCloneStateAsync(connection, transaction, proposal, ct); },
        "restore_clone_reconciliation_required");
    await using (var retained = new NpgsqlCommand("SELECT count(*) FROM assets WHERE owner_id=$1", connection, transaction))
    {
        retained.Parameters.AddWithValue(manifest.OwnerId);
        Check(Convert.ToInt64(await retained.ExecuteScalarAsync(ct)) > 0, "Unbound clone rejection erased the restored assets.");
    }
    await RestoreSessionStore.ImportVerifiedPointAsync(connection, transaction, envelope, trust, ct);
    await RestoreSessionStore.ImportProposalAsync(connection, transaction, proposal, ct);
    Check(await RecoveryRestoreRunner.ReadExistingCloneStateAsync(connection, transaction, proposal, ct) == "requested",
        "Native clone cannot resume its exact immutable request.");
    var changed = RecoveryRestoreProposalV1.Create(proposal.SessionId, proposal.OwnerId, proposal.LibraryId,
        proposal.RecoveryPointId, proposal.RequestedScope with { Reason = "changed input" }, proposal.ExpiresAtUtc);
    await RejectCode(async () => { _ = await RecoveryRestoreRunner.ReadExistingCloneStateAsync(connection, transaction, changed, ct); },
        "restore_clone_reconciliation_required");
    await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId, "requested", "restoring", now, ct);
    await RestoreSessionStore.RecordStateAsync(connection, transaction, proposal.SessionId, "restoring", "validating", now, ct);
    Check(await RecoveryRestoreRunner.AppendValidatedCloneAsync(connection, transaction, proposal, verified, hash, now, ct) == 1,
        "Native retained clone did not persist a final-manifest validation revision.");
    Check(await RecoveryRestoreRunner.AppendValidatedCloneAsync(connection, transaction, proposal, verified, hash, now, ct) == 1,
        "Native retained clone validation replay added a revision.");
    Check(await RecoveryRestoreRunner.ReadExistingCloneStateAsync(connection, transaction, proposal, ct) == "validating",
        "Native retained validation granted normal access or promotion.");
    // Leave the native corruption corpus unchanged. These fixture attestations are
    // database-boundary tests, not real owner access or full ExecuteAsync evidence.
    await transaction.RollbackAsync(ct);
}

static async Task CandidateChecks(NpgsqlConnection connection, RecoveryPointManifestV1 manifest,
    RSA auditRsa, RecoverySigningTrust auditTrust, VerifiedRecoveryAuditGenesis genesis, RecoveryAuditHeadRequirementV1 heads,
    string migrationDigest, string recoveryAccountId, CancellationToken ct)
{
    var id = Guid.NewGuid();
    var fixtureHash = new string('a', 64);
    manifest = manifest with { Components = [
        new("database.dump",RecoveryDataClass.DatabaseSnapshot,"s3://native-capture/database.dump","dump-v1",fixtureHash,1),
        new("objects.json",RecoveryDataClass.ObjectInventory,"s3://native-capture/objects.json","inventory-v1",fixtureHash,1),
        new("release-bindings.json",RecoveryDataClass.ReleaseArtifact,"s3://native-capture/release.json","release-v1",fixtureHash,1)] };
    var digest = manifest.Digest();
    var settings = new Dictionary<string,string>
    {
        ["RecoveryOnly"]="true",["TargetBucket"]="isolated-native-target",["SourceBucket"]="native-source",
        ["DestinationBucket"]="recovery-payload",["EvidenceBucket"]="recovery-evidence",
        ["SourceAccountId"]="111122223333",["SourceRegion"]="ap-southeast-1",
        ["DestinationAccountId"]=recoveryAccountId,["DestinationRegion"]="ap-southeast-2",
        ["ConfigurationDigest"]=fixtureHash,["ApiBuildDigest"]=fixtureHash,
        ["OwnerUserPoolIssuer"]="https://native-pool.example/issuer",["OwnerUserPoolClientId"]="native-client"
    };
    var old = settings.Keys.ToDictionary(key=>key,key=>Environment.GetEnvironmentVariable("Recovery__"+key));
    foreach(var (key,value) in settings) Environment.SetEnvironmentVariable("Recovery__"+key,value);
    try
    {
    var configuration = new RecoveryWorkerConfiguration("drill");
    var now = DateTimeOffset.UtcNow;
    var pointer = new CopyPointer(manifest.RecoveryPointId,manifest.CaptureId,manifest.OwnerId,manifest.LibraryId,
        "111122223333","ap-southeast-1",recoveryAccountId,"ap-southeast-2","native-capture","manifest","v1",digest,
        "recovery-evidence","manifest","v1",digest,"checkpoint","v1",digest,1,now,"access_incomplete");
    var point = new VerifiedCopiedPoint(pointer,manifest,null,null!,[],digest,digest,null);
    var auditPayload = new RecoveryAuditHeadProofPayloadV1(1,manifest.RecoveryPointId,manifest.CaptureId,manifest.OwnerId,
        manifest.LibraryId,digest,digest,manifest.SnapshotCutoffUtc,manifest.SnapshotLsn,heads.PreviouslyVerified!,
        heads.Minimum,null,genesis.ArtifactSha256,now,configuration.WorkerBuildDigest);
    var auditBytes = RecoveryAuditLedgerCodec.SerializeAuditHeadProofPayload(auditPayload);
    var auditEnvelope = RecoveryAuditLedgerCodec.CreateAuditHeadProofEnvelope(auditPayload,"native-fixture",
        new(auditTrust.KeyId,auditTrust.PublicKeySha256,auditRsa.SignHash(SHA256.HashData(auditBytes),
            HashAlgorithmName.SHA256,RSASignaturePadding.Pss)));
    var proof = RecoveryAuditLedgerCodec.VerifyAuditHeadProof(auditEnvelope,auditTrust);
    await RecoveryCandidateStore.CreateAsync(connection,id,connection.Database!,configuration,point,proof,ct);
    await RecoveryCandidateStore.CreateAsync(connection,id,connection.Database!,configuration,point,proof,ct);
    Environment.SetEnvironmentVariable("Recovery__ConfigurationDigest",new string('b',64));
    await RejectCode(()=>RecoveryCandidateStore.CreateAsync(connection,id,connection.Database!,configuration,point,proof,ct),
        "candidate_replay_binding_mismatch");
    Environment.SetEnvironmentVariable("Recovery__ConfigurationDigest",fixtureHash);
    async Task RejectSql(string sql)
    {
        try { await Execute(connection, sql, ct); }
        catch (PostgresException exception) when (exception.SqlState == "P0001") { return; }
        throw new InvalidOperationException("Candidate mutation was accepted: " + sql);
    }
    await RejectSql($"UPDATE recovery_candidate_sessions SET owner_id='foreign' WHERE candidate_id='{id:D}'");
    await RejectSql($"UPDATE recovery_candidate_sessions SET state='owner_access_verified' WHERE candidate_id='{id:D}'");
    var receipts = manifest.Objects.Select(item => new RecoveryMaterializedObjectV1("native-source", item.SourceKey,
        item.SourceVersionId, item.Sha256, item.ByteLength, "isolated-native-target", "sha256/" + item.Sha256,
        "target-version-" + item.Sha256)).ToArray();
    await RecoveryCandidateStore.RebindAsync(connection, id, receipts, ct);
    await RecoveryCandidateStore.RebindAsync(connection, id, receipts, ct);
    var validation = await RecoveryDrillRunner.ValidateMaterializedCandidateAsync(connection, manifest, receipts,
        "native-source", "isolated-native-target", auditTrust, genesis, "/migrations", migrationDigest,
        recoveryAccountId, heads, configuration: null, ct);
    await using (var corrupt = IngestDb.Command(connection,
        "UPDATE content_objects SET storage_version='wrong-target-version' WHERE sha256=$1", receipts[0].Sha256))
        await corrupt.ExecuteNonQueryAsync(ct);
    await RejectCode(() => RecoveryDrillRunner.ValidateMaterializedCandidateAsync(connection, manifest, receipts,
        "native-source", "isolated-native-target", auditTrust, genesis, "/migrations", migrationDigest,
        recoveryAccountId, heads, configuration: null, ct), "candidate_materialized_closure_mismatch");
    await using (var reset = IngestDb.Command(connection,
        "UPDATE content_objects SET storage_version=$1 WHERE sha256=$2", receipts[0].DestinationVersionId, receipts[0].Sha256))
        await reset.ExecuteNonQueryAsync(ct);
    await RejectCode(() => RecoveryCandidateStore.RebindAsync(connection, id,
        receipts.Select((item, index) => index == 0 ? item with { DestinationVersionId = "substituted" } : item).ToArray(), ct),
        "candidate_receipt_replay_mismatch");
    await RejectSql($"UPDATE recovery_candidate_sessions SET state='owner_access_pending' WHERE candidate_id='{id:D}'");
    await RecoveryCandidateStore.RecordValidationAsync(connection, id, digest, digest, validation, ct);
    await RecoveryCandidateStore.RecordValidationAsync(connection, id, digest, digest, validation, ct);
    await RejectCode(() => RecoveryCandidateStore.RecordValidationAsync(connection, id, digest, digest, new string('f', 64), ct),
        "candidate_validation_replay_mismatch");
    await RejectSql($"DELETE FROM recovery_candidate_materialization_receipts WHERE candidate_id='{id:D}'");
    await RejectSql($"DELETE FROM recovery_candidate_validation_revisions WHERE candidate_id='{id:D}'");
    Check((string?)await Scalar(connection, $"SELECT state FROM recovery_candidate_sessions WHERE candidate_id='{id:D}'", ct)
        == "owner_access_pending", "Candidate must remain unqualified without signed owner proof.");
    var approvedAt = now.AddTicks(-(now.Ticks%10));
    var identity = new RecoveryIdentityApprovalV1(1,Guid.NewGuid(),settings["OwnerUserPoolIssuer"],settings["OwnerUserPoolClientId"],
        "native-recovery-subject",manifest.OwnerId,"native-independent-owner",auditTrust.KeyId,approvedAt);
    var identityArtifact = RecoveryEvidenceCodec.SerializeCanonical(new RecoveryIdentityApprovalArtifactV1(identity,
        Convert.ToBase64String(auditRsa.SignData(RecoveryEvidenceCodec.SerializeCanonical(identity),HashAlgorithmName.SHA256,
            RSASignaturePadding.Pss))));
    await RecoveryCandidateIdentity.InstallAsync(connection,id,identityArtifact,auditTrust,configuration,ct);
    await RecoveryCandidateIdentity.InstallAsync(connection,id,identityArtifact,auditTrust,configuration,ct);
    Check(Convert.ToInt64(await Scalar(connection,$"SELECT count(*) FROM recovery_identity_mappings WHERE mapping_id='{identity.MappingId:D}'",ct))==1,
        "Signed identity installation must replay without duplication.");
    await CandidateArtifactChecks.RunAsync(connection,configuration,id,point,receipts,auditRsa,auditTrust,ct);
    await CandidateRuntimeChecks(connection,id,ct);
    await using var restore = IngestDb.Command(connection, """
        UPDATE content_objects c SET storage_key=r.source_key,storage_version=r.source_version_id
        FROM recovery_candidate_materialization_receipts r WHERE r.candidate_id=$1 AND c.sha256=r.sha256
        """, id);
    await restore.ExecuteNonQueryAsync(ct);
    await FrozenCaptureChecks(connection,configuration,manifest,ct);
    }
    finally { foreach(var (key,value) in old) Environment.SetEnvironmentVariable("Recovery__"+key,value); }
}

static async Task CandidateRuntimeChecks(NpgsqlConnection connection, Guid candidateId, CancellationToken ct)
{
    const string runtimePassword = "NativeFixturePasswordOnly1234567890";
    await RecoveryCandidateRuntimeDatabase.ProvisionAsync(connection,candidateId,"platform_runtime",runtimePassword,ct);
    await RecoveryCandidateRuntimeDatabase.ProvisionAsync(connection,candidateId,"platform_runtime",runtimePassword,ct);
    var runtimeString = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
        { Username="platform_runtime",Password=runtimePassword }.ConnectionString;
    await using var runtime = new NpgsqlConnection(runtimeString);
    await runtime.OpenAsync(ct);
    Check((string?)await Scalar(runtime,"SHOW default_transaction_read_only",ct)=="on",
        "Every API login must start read-only.");
    Check(Convert.ToInt64(await Scalar(runtime,"SELECT count(*) FROM recovery_identity_mappings",ct))>0,
        "Runtime login must read installed mappings.");
    await using(var set = new NpgsqlCommand("SET default_transaction_read_only=off",runtime))
        await set.ExecuteNonQueryAsync(ct);
    foreach(var sql in new[] { "UPDATE assets SET state=state", "CREATE TABLE public.runtime_escape(id integer)",
        "SET ROLE postgres", "DELETE FROM recovery_candidate_sessions" })
    {
        try { await using var denied=new NpgsqlCommand(sql,runtime); await denied.ExecuteNonQueryAsync(ct); }
        catch(PostgresException error) when(error.SqlState=="42501") { continue; }
        throw new InvalidOperationException("Runtime privilege escape accepted: "+sql);
    }
    await using(var unsafeGrant=new NpgsqlCommand("GRANT UPDATE ON assets TO platform_runtime",connection))
        await unsafeGrant.ExecuteNonQueryAsync(ct);
    await RejectCode(()=>RecoveryCandidateRuntimeDatabase.ProvisionAsync(connection,candidateId,
        "platform_runtime",runtimePassword,ct),"candidate_runtime_role_unsafe");
    await using(var revoke=new NpgsqlCommand("REVOKE UPDATE ON assets FROM platform_runtime",connection))
        await revoke.ExecuteNonQueryAsync(ct);
}

static async Task FrozenCaptureChecks(NpgsqlConnection connection, RecoveryWorkerConfiguration configuration,
    RecoveryPointManifestV1 manifest, CancellationToken ct)
{
    var original=manifest.Objects.First(item=>item.DataClass==RecoveryDataClass.OriginalImage);
    var asset=original.LogicalReferences.First(item=>item.AssetId is not null).AssetId!.Value;
    var now=DateTimeOffset.UtcNow;
    now=now.AddTicks(-(now.Ticks%10));
    var proposal=RecoveryPurgeProposalV1.Create(Guid.NewGuid(),Guid.NewGuid(),manifest.OwnerId,manifest.LibraryId,
        asset,null,"original",original.Sha256,1,1,now,now,now.AddHours(1));
    var freeze=new RecoveryPurgeFreezeAcknowledgementV1(1,proposal.OperationId,proposal.EventId,proposal.OwnerId,
        proposal.LibraryId,asset,null,"original",original.Sha256,1,1,proposal.PlanDigest,proposal.OwnerId,now,now);
    var freezeBytes=RecoveryEvidenceCodec.SerializeCanonical(freeze);
    await using var transaction=await connection.BeginTransactionAsync(ct);
    await using (var seed=IngestDb.Command(connection,"""
        INSERT INTO recovery_purge_proposals(operation_id,event_id,owner_id,library_id,asset_id,category,content_sha256,
            expected_lifecycle_version,lifecycle_generation,proposed_at_utc,eligible_at_utc,expires_at_utc,proposal_digest,
            canonical_proposal,phase,primary_actor,reason_code)
        VALUES($1,$2,$3,$4,$5,'original',$6,1,1,$7,$7,$8,$9,$10,'executing',$3,'native-freeze');
        INSERT INTO recovery_purge_freezes(operation_id,event_id,owner_id,library_id,asset_id,category,content_sha256,
            expected_lifecycle_version,lifecycle_generation,proposal_digest,freeze_ack_sha256,canonical_freeze_ack,
            frozen_by,auth_time_utc,frozen_at_utc)
        VALUES($1,$2,$3,$4,$5,'original',$6,1,1,$9,$11,$12,$3,$7,$7)
        """,proposal.OperationId,proposal.EventId,proposal.OwnerId,proposal.LibraryId,asset,original.Sha256,now,
        proposal.ExpiresAtUtc,proposal.PlanDigest,RecoveryEvidenceCodec.SerializeCanonical(proposal),
        RecoveryEvidenceCodec.Hash(freezeBytes),freezeBytes))
    {
        seed.Transaction=transaction;
        await seed.ExecuteNonQueryAsync(ct);
    }
    var omissions=await RecoveryPurgeCapture.ReadAuthorizedOmissionsAsync(connection,transaction,configuration,
        manifest.OwnerId,manifest.LibraryId,DateTimeOffset.UtcNow,ct);
    Check(omissions.Count==0,"Reversible freeze must permit a full post-freeze capture without authorizing omissions.");
    var closure=await CaptureRunner.ReadObjectClosureAsync(connection,transaction,"native-source",manifest.OwnerId,
        manifest.LibraryId,omissions,ct);
    RecoveryObjectClosureValidator.RequireExactMatch(manifest.Objects,closure);
    await using (var corrupt=IngestDb.Command(connection,"UPDATE recovery_purge_freezes SET freeze_ack_sha256=repeat('f',64) WHERE operation_id=$1",
        proposal.OperationId))
    {
        corrupt.Transaction=transaction;
        await corrupt.ExecuteNonQueryAsync(ct);
    }
    await RejectCode(()=>RecoveryPurgeCapture.ReadAuthorizedOmissionsAsync(connection,transaction,configuration,
        manifest.OwnerId,manifest.LibraryId,DateTimeOffset.UtcNow,ct),"purge_freeze_ack_binding_invalid");
    await transaction.RollbackAsync(ct);
}

static async Task RejectTls(string connectionString, CancellationToken ct)
{
    await using var connection = new NpgsqlConnection(connectionString);
    try { await connection.OpenAsync(ct); }
    catch (NpgsqlException exception) when (exception.InnerException is System.Security.Authentication.AuthenticationException) { return; }
    throw new InvalidOperationException("TLS certificate validation did not reject the fixture.");
}
static void WriteCertificates(string directory)
{
    var before = DateTimeOffset.UtcNow.AddMinutes(-5);
    var after = before.AddDays(2);
    using var rootKey = RSA.Create(2048);
    var request = new CertificateRequest("CN=Native Recovery Fixture CA", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
    using var root = request.CreateSelfSigned(before, after);
    File.WriteAllText(Path.Combine(directory, "ca.pem"), root.ExportCertificatePem());
    using var serverKey = RSA.Create(2048);
    var serverRequest = new CertificateRequest("CN=recovery-postgres", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("recovery-postgres");
    serverRequest.CertificateExtensions.Add(names.Build());
    serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
    serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new System.Security.Cryptography.OidCollection
    {
        new("1.3.6.1.5.5.7.3.1")
    }, true));
    using var server = serverRequest.Create(root, before, after.AddMinutes(-1), RandomNumberGenerator.GetBytes(16));
    File.WriteAllText(Path.Combine(directory, "server.pem"), server.ExportCertificatePem());
    File.WriteAllText(Path.Combine(directory, "server-key.pem"), serverKey.ExportPkcs8PrivateKeyPem());
    using var otherKey = RSA.Create(2048);
    using var other = new CertificateRequest("CN=Untrusted Native Fixture CA", otherKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        .CreateSelfSigned(before, after);
    File.WriteAllText(Path.Combine(directory, "untrusted-ca.pem"), other.ExportCertificatePem());
}
