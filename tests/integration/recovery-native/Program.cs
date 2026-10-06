using System.Data;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
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
foreach (var migration in Directory.EnumerateFiles("/migrations", "*.sql").Order(StringComparer.Ordinal))
    await Execute(source, await File.ReadAllTextAsync(migration, ct), ct);

const string owner = "native-owner";
const string library = "native-library";
var asset = Guid.NewGuid();
var sha = new string('1', 64);
var child = new string('2', 64);
var parent = new string('3', 64);
var foreign = new string('4', 64);
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
        INSERT INTO assets(id,owner_id,original_sha256,state,format) VALUES($2,$3,$1,'active','dng');
        INSERT INTO asset_paths(id,asset_id,owner_id,display_path,collision_key,active)
        VALUES(gen_random_uuid(),$2,$3,'before.dng','BEFORE.DNG',true);
        INSERT INTO vc_commits(id,owner_id,library_id,author_id,authored_at,received_at,message,contract_version,policy_version,manifest_bytes)
        VALUES($4,$3,$5,$3,now(),now(),'child',1,1,convert_to(jsonb_build_object('parents',jsonb_build_array($6::text),'receivedAt',now())::text,'UTF8')),
              ($6,$3,$5,$3,now(),now(),'parent',1,1,convert_to(jsonb_build_object('parents','[]'::jsonb,'receivedAt',now())::text,'UTF8')),
              ($7,'foreign-owner',$5,'foreign-owner',now(),now(),'foreign',1,1,convert_to(jsonb_build_object('parents','[]'::jsonb,'receivedAt',now())::text,'UTF8'));
        INSERT INTO vc_commit_parents(commit_id,ordinal,parent_id) VALUES($4,0,$6);
        INSERT INTO vc_commit_entries(commit_id,ordinal,asset_id,change_kind,content_sha256,display_path,lifecycle_state)
        VALUES($4,0,$2,'added',$1,'before.dng','active');
        """, sha, asset, owner, child, library, parent, foreign);
    command.Transaction = seed;
    await command.ExecuteNonQueryAsync(ct);
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
    await IngestDb.RecordRecoveryChangeAsync(source, seed, owner, library, "native-seed", "fixture_seed",
        "asset", asset.ToString(), [sha], DateTimeOffset.UtcNow, ct);
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
Check(Convert.ToInt64(await Scalar(source, "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id='native-owner'", ct)) == 2,
    "Concurrent source mutation did not commit.");

var manifest = new RecoveryPointManifestV1(1, Guid.NewGuid(), Guid.NewGuid(), owner, library, "ap-southeast-1",
    DateTimeOffset.UtcNow, exported.CutoffUtc, exported.SnapshotId, exported.Lsn, new string('a', 64), watermark,
    [], objects, counts.Select(pair => new RecoveryClassCoverage(pair.Key, pair.Value.ItemCount, pair.Value.ByteLength)).ToArray());
var recovery = new RecoveryDrillRunner.RecoveryDatabaseConnection("recovery-postgres", 5432, "postgres", password,
    "/fixtures/ca.pem", connectionString);
const string targetName = "native_recovered";
await RecoveryDrillRunner.CreateDatabaseAsync(recovery, targetName, ct);
try
{
    await RecoveryDrillRunner.RunPostgresToolAsync("pg_restore", ["--exit-on-error", "--no-owner", "--no-acl", "--dbname", targetName, dump], recovery, ct);
    var targetString = new NpgsqlConnectionStringBuilder(connectionString) { Database = targetName }.ConnectionString;
    await using var target = new NpgsqlConnection(targetString);
    await target.OpenAsync(ct);
    Check((string?)await Scalar(target, "SELECT display_path FROM asset_paths WHERE owner_id='native-owner'", ct) == "before.dng",
        "Native dump included a mutation committed after its exported snapshot.");
    Check(Convert.ToInt64(await Scalar(target, "SELECT last_sequence FROM recovery_change_watermarks WHERE owner_id='native-owner'", ct)) == 1,
        "Dump and inventory watermarks differ.");
    await RecoveryDrillRunner.ValidateRestoredPointAsync(recovery, targetName, manifest, ct);

    await using (var corruption = new NpgsqlCommand("UPDATE vc_commit_parents SET parent_id=$1 WHERE commit_id=$2", target))
    {
        // Simulate a damaged restored artifact; production immutable triggers stay enabled.
        await Execute(target, "ALTER TABLE vc_commit_parents DISABLE TRIGGER ALL", ct);
        corruption.Parameters.AddWithValue(foreign);
        corruption.Parameters.AddWithValue(child);
        await corruption.ExecuteNonQueryAsync(ct);
    }
    await RejectCode(() => RecoveryDrillRunner.ValidateRestoredPointAsync(recovery, targetName, manifest, ct), "restored_version_graph_invalid");
    await Execute(target, "DROP TABLE assets CASCADE", ct);
    await RejectCode(() => RecoveryDrillRunner.ValidateRestoredPointAsync(recovery, targetName, manifest, ct), "restored_database_schema_incomplete");
}
finally
{
    await RecoveryDrillRunner.DropDatabaseAsync(recovery, targetName, ct);
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
