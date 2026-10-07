import { createHash } from 'node:crypto';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { App, Environment } from 'aws-cdk-lib';
import { PlatformFoundationStack } from './platform-foundation-stack.js';
import { RecoveryStack } from './recovery/recovery-stack.js';

const app = new App();
const offline = app.node.tryGetContext('offline') === 'true';
const requiredContext = (name: string, offlineValue: string): string => {
  const value = app.node.tryGetContext(name);
  if (typeof value === 'string' && value.trim()) return value;
  if (offline) return offlineValue;
  throw new Error(`CDK context ${name} is required for deployment.`);
};
const environmentName = app.node.tryGetContext('environment') ?? 'nonprod';
const region = app.node.tryGetContext('region') ?? 'ap-southeast-1';
const recoveryEnabledValue = app.node.tryGetContext('recovery');
if (recoveryEnabledValue !== undefined && recoveryEnabledValue !== 'true' && recoveryEnabledValue !== 'false') {
  throw new Error('CDK context recovery must be exactly true or false.');
}
const recoveryEnabled = recoveryEnabledValue === 'true';
const recoveryStackOnlyValue = app.node.tryGetContext('recoveryStackOnly');
if (recoveryStackOnlyValue !== undefined && recoveryStackOnlyValue !== 'true' && recoveryStackOnlyValue !== 'false') {
  throw new Error('CDK context recoveryStackOnly must be exactly true or false.');
}
const recoveryStackOnly = recoveryEnabled && recoveryStackOnlyValue === 'true';
const primaryAccountId = recoveryEnabled ? requiredContext('primaryAccountId', '111122223333') : undefined;
const recoveryAccountId = recoveryEnabled ? requiredContext('recoveryAccountId', '444455556666') : undefined;
if (recoveryEnabled && (!/^\d{12}$/.test(primaryAccountId!) || !/^\d{12}$/.test(recoveryAccountId!) || primaryAccountId === recoveryAccountId)) {
  throw new Error('Recovery requires distinct, real 12-digit primaryAccountId and recoveryAccountId values.');
}
if (recoveryEnabled && region !== 'ap-southeast-1') throw new Error('The primary recovery integration is fixed to ap-southeast-1.');
const env: Environment = recoveryEnabled ? { account: primaryAccountId, region } : { region };
if (offline && recoveryEnabled) {
  app.node.setContext(`availability-zones:account=${primaryAccountId}:region=ap-southeast-1`, ['ap-southeast-1a', 'ap-southeast-1b']);
  app.node.setContext(`availability-zones:account=${recoveryAccountId}:region=ap-southeast-2`, ['ap-southeast-2a', 'ap-southeast-2b']);
}
const recoverySchedulesEnabled = app.node.tryGetContext('recoverySchedulesEnabled') === 'true';
const captureDbSecretKmsKeyArn = app.node.tryGetContext('captureDbSecretKmsKeyArn');
const recoverySchedulesValue = app.node.tryGetContext('recoverySchedulesEnabled');
if (recoverySchedulesValue !== undefined && recoverySchedulesValue !== 'true' && recoverySchedulesValue !== 'false') {
  throw new Error('CDK context recoverySchedulesEnabled must be exactly true or false.');
}
const numericContext = (name: string, offlineValue: string): number => {
  const value = Number(requiredContext(name, offlineValue));
  if (!Number.isFinite(value) || value <= 0) throw new Error(`CDK context ${name} must be a positive number.`);
  return value;
};

const migrationDirectory = resolve(dirname(fileURLToPath(import.meta.url)), '../../..', 'apps/api/Database/Migrations');
const computedMigrationDigest = (() => {
  const digest = createHash('sha256');
  for (const name of readdirSync(migrationDirectory).filter(name => name.endsWith('.sql')).sort()) {
    digest.update(name, 'utf8');
    digest.update('\0', 'utf8');
    digest.update(readFileSync(resolve(migrationDirectory, name)));
  }
  return digest.digest('hex');
})();
const recoveryMigrationDigest = requiredContext('recoveryMigrationDigest', computedMigrationDigest).toLowerCase();
if (!/^[a-f0-9]{64}$/.test(recoveryMigrationDigest) || recoveryMigrationDigest !== computedMigrationDigest) {
  throw new Error('recoveryMigrationDigest must match the ordered SQL artifacts embedded in the recovery-worker image.');
}
const offlineDatabaseRootCertificate = '-----BEGIN CERTIFICATE-----\noffline-only-placeholder\n-----END CERTIFICATE-----';
const offlineDatabaseRootCertificateSha256 = createHash('sha256').update(offlineDatabaseRootCertificate, 'utf8').digest('hex');
const offlinePlaceholderSpkiBase64 = 'A'.repeat(128);
const databaseRootCertificate = requiredContext('recoveryDatabaseRootCertificate', offlineDatabaseRootCertificate);
const computedDatabaseRootCertificateSha256 = createHash('sha256').update(databaseRootCertificate, 'utf8').digest('hex');
const databaseRootCertificateSha256 = requiredContext('recoveryDatabaseRootCertificateSha256', offlineDatabaseRootCertificateSha256).toLowerCase();
if (!/^[a-f0-9]{64}$/.test(databaseRootCertificateSha256) || databaseRootCertificateSha256 !== computedDatabaseRootCertificateSha256) {
  throw new Error('recoveryDatabaseRootCertificateSha256 must match the exact database root certificate PEM bytes.');
}
const recoveryApiBuildDigest = requiredContext('recoveryApiImageDigest', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb').toLowerCase();
const recoveryIngestWorkerBuildDigest = requiredContext('recoveryIngestWorkerImageDigest', 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee').toLowerCase();
const recoveryRendererBuildDigest = requiredContext('recoveryRendererImageDigest', 'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff').toLowerCase();
const recoveryTemplateDigest = requiredContext('recoveryTemplateDigest', '9999999999999999999999999999999999999999999999999999999999999999').toLowerCase();
const recoveryConfigurationDigest = requiredContext('recoveryConfigurationDigest', '8888888888888888888888888888888888888888888888888888888888888888').toLowerCase();
for (const [name, digest] of [
  ['recoveryApiImageDigest', recoveryApiBuildDigest],
  ['recoveryIngestWorkerImageDigest', recoveryIngestWorkerBuildDigest],
  ['recoveryRendererImageDigest', recoveryRendererBuildDigest],
  ['recoveryTemplateDigest', recoveryTemplateDigest],
  ['recoveryConfigurationDigest', recoveryConfigurationDigest],
] as const) {
  if (!/^[a-f0-9]{64}$/.test(digest)) throw new Error(`CDK context ${name} must be a SHA-256 digest.`);
}
const recoveryLibraryId = recoveryEnabled ? requiredContext('recoveryLibraryId', '00000000-0000-0000-0000-000000000011') : undefined;
const recoveryOwnerIdValue = recoveryEnabled ? app.node.tryGetContext('recoveryOwnerId') : undefined;
if (recoveryOwnerIdValue !== undefined && (typeof recoveryOwnerIdValue !== 'string' || !recoveryOwnerIdValue.trim())) {
  throw new Error('CDK context recoveryOwnerId, when provided, must be a non-empty provisioned owner identifier.');
}
const recoveryOwnerId = typeof recoveryOwnerIdValue === 'string' ? recoveryOwnerIdValue : undefined;

if (!recoveryStackOnly) {
  new PlatformFoundationStack(app, `PlatformFoundation-${environmentName}`, {
    env,
    offlineSynthesis: offline,
    environmentName,
    monthlyBudgetUsd: Number(requiredContext('monthlyBudgetUsd', '20')),
    ownerEmail: requiredContext('ownerEmail', 'owner@example.invalid'),
    alertEmail: requiredContext('alertEmail', 'alerts@example.invalid'),
    webOrigin: requiredContext('webOrigin', 'https://example.invalid'),
    cognitoDomainPrefix: requiredContext('cognitoDomainPrefix', `platform-${environmentName}-example`),
    deploymentRoleArn: requiredContext('deploymentRoleArn', 'arn:aws:iam::111122223333:role/github-actions-nonprod-deploy'),
    ...(recoveryEnabled ? {
      recoveryIntegration: {
        recoveryAccountId: recoveryAccountId!,
        recoveryLibraryId: recoveryLibraryId!,
        ...(recoveryOwnerId ? { recoveryOwnerId } : {}),
        sourceDatabaseSecretArn: requiredContext('captureDbSecretArn', 'arn:aws:secretsmanager:ap-southeast-1:111122223333:secret:recovery-db-reader'),
        ...(typeof captureDbSecretKmsKeyArn === 'string' && captureDbSecretKmsKeyArn.trim() ? { sourceDatabaseSecretKmsKeyArn: captureDbSecretKmsKeyArn } : {}),
        captureImageTag: requiredContext('recoveryImageTag', '0123456789abcdef0123456789abcdef01234567'),
        captureImageDigest: requiredContext('recoveryImageDigest', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'),
        apiBuildDigest: recoveryApiBuildDigest,
        ingestWorkerBuildDigest: recoveryIngestWorkerBuildDigest,
        rendererBuildDigest: recoveryRendererBuildDigest,
        recoveryTemplateDigest,
        migrationDigest: recoveryMigrationDigest,
        configurationDigest: recoveryConfigurationDigest,
        databaseRootCertificate,
        databaseRootCertificateSha256,
        ownerApprovalTrustKeyId: requiredContext('recoveryOwnerApprovalKeyId', 'arn:aws:kms:ap-southeast-2:444455556666:key/12345678-1234-1234-1234-123456789abc'),
        ownerApprovalTrustPublicKeySpkiBase64: requiredContext('recoveryOwnerApprovalPublicKeySpkiBase64', offlinePlaceholderSpkiBase64),
        ownerApprovalTrustPublicKeySha256: requiredContext('recoveryOwnerApprovalPublicKeySha256', 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'),
        observationSigningKeyId: requiredContext('recoveryObservationSigningKeyId', 'arn:aws:kms:ap-southeast-2:444455556666:key/12345678-1234-1234-1234-123456789abc'),
        observationPublicKeySpkiBase64: requiredContext('recoveryObservationPublicKeySpkiBase64', offlinePlaceholderSpkiBase64),
        observationPublicKeySha256: requiredContext('recoveryObservationPublicKeySha256', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'),
        captureEphemeralStorageGiB: numericContext('captureEphemeralStorageGiB', '21'),
        schedulesEnabled: recoverySchedulesEnabled,
      },
    } : {}),
  });
}

if (recoveryEnabled) {
  const primaryObservationQueueName = `platform-${environmentName}-recovery-observations`;
  const primaryObservationQueueArn = `arn:aws:sqs:${region}:${primaryAccountId}:${primaryObservationQueueName}`;
  const primaryObservationQueueUrl = `https://sqs.${region}.amazonaws.com/${primaryAccountId}/${primaryObservationQueueName}`;
  const alertEmails = requiredContext('recoveryAlertEmails', offline ? 'alerts@example.invalid' : '').split(',').map(value => value.trim()).filter(Boolean);
  const githubRepository = requiredContext('githubRepository', 'FSN-Private/software_construction');
  const githubOidcSubject = requiredContext('githubOidcSubject', `repo:${githubRepository}:environment:recovery-drill`);
  new RecoveryStack(app, `Recovery-${environmentName}`, {
    env: { account: recoveryAccountId, region: requiredContext('recoveryRegion', 'ap-southeast-2') },
    environmentName,
    recoveryLibraryId: recoveryLibraryId!,
    ...(recoveryOwnerId ? { recoveryOwnerId } : {}),
    recoveryAccountId: recoveryAccountId!,
    primaryAccountId: primaryAccountId!,
    primaryAccountAlias: requiredContext('primaryAccountAlias', 'primary-nonprod'),
    recoveryAccountAlias: requiredContext('recoveryAccountAlias', 'recovery-nonprod'),
    primaryRegion: region,
    primaryCaptureBucketName: `platform-${environmentName}-${primaryAccountId}-recovery-capture`,
    primaryIngestBucketName: requiredContext('primaryIngestBucketName', `platform-${environmentName}-${primaryAccountId}-ingest`),
    primaryKmsKeyArn: requiredContext('primaryKmsKeyArn', 'arn:aws:kms:ap-southeast-1:111122223333:key/12345678-1234-1234-1234-123456789abc'),
    primaryObservationQueueArn,
    primaryObservationQueueUrl,
    ownerEmail: requiredContext('recoveryOwnerEmail', 'owner@example.invalid'),
    alertEmails,
    monthlyBudgetUsd: numericContext('recoveryMonthlyBudgetUsd', '100'),
    databaseInstanceType: requiredContext('recoveryDatabaseInstanceType', 't4g.medium'),
    databaseAllocatedStorageGiB: numericContext('recoveryDatabaseAllocatedStorageGiB', '20'),
    databaseMaxStorageGiB: numericContext('recoveryDatabaseMaxStorageGiB', '100'),
    workerEphemeralStorageGiB: numericContext('recoveryWorkerEphemeralStorageGiB', '21'),
    schedulesEnabled: recoverySchedulesEnabled,
    allowOfflinePlaceholders: offline,
    githubRepository,
    githubOidcSubject,
    recoveryImageTag: requiredContext('recoveryImageTag', '0123456789abcdef0123456789abcdef01234567'),
    recoveryImageDigest: requiredContext('recoveryImageDigest', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'),
    recoveryApiImageDigest: requiredContext('recoveryApiImageDigest', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'),
    recoveryApiBuildDigest,
    recoveryIngestWorkerBuildDigest,
    recoveryRendererBuildDigest,
    recoveryTemplateDigest,
    migrationDigest: recoveryMigrationDigest,
    configurationDigest: recoveryConfigurationDigest,
    databaseRootCertificate,
    databaseRootCertificateSha256,
    auditGenesisVersionId: requiredContext('recoveryAuditGenesisVersionId', 'offline-audit-genesis-version'),
    auditGenesisSha256: requiredContext('recoveryAuditGenesisSha256', 'abababababababababababababababababababababababababababababababab'),
    auditGenesisByteLength: numericContext('recoveryAuditGenesisByteLength', '512'),
    recoverySigningPublicKeySpkiBase64: requiredContext('recoveryObservationPublicKeySpkiBase64', offlinePlaceholderSpkiBase64),
    recoverySigningPublicKeySha256: requiredContext('recoveryObservationPublicKeySha256', 'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'),
    ownerApprovalRoleArn: requiredContext('recoveryOwnerApprovalRoleArn', 'arn:aws:iam::444455556666:role/platform-recovery-owner-approval'),
    purgeOperatorArn: requiredContext('recoveryPurgeOperatorArn', 'arn:aws:iam::444455556666:role/platform-recovery-purge-operator'),
    ownerApprovalPublicKeySpkiBase64: requiredContext('recoveryOwnerApprovalPublicKeySpkiBase64', offlinePlaceholderSpkiBase64),
    ownerApprovalPublicKeySha256: requiredContext('recoveryOwnerApprovalPublicKeySha256', 'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd'),
  });
}
