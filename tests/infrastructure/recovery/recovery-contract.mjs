import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { verifyRecoveryTemplate } from '../../../scripts/verify-recovery-template.mjs';

const foundation = JSON.parse(readFileSync('packages/infrastructure/cdk.out-recovery/PlatformFoundation-nonprod.template.json', 'utf8'));
const recoveryBytes = readFileSync('packages/infrastructure/cdk.out-recovery/Recovery-nonprod.template.json');
const recovery = JSON.parse(recoveryBytes.toString('utf8'));
const recoveryDigest = createHash('sha256').update(recoveryBytes).digest('hex');
assert.deepEqual(verifyRecoveryTemplate(recoveryBytes, recoveryDigest), [
  { ParameterKey: 'RecoveryTemplateDigest', ParameterValue: recoveryDigest },
]);
assert.throws(() => verifyRecoveryTemplate(Buffer.concat([recoveryBytes, Buffer.from('\n')]), recoveryDigest), /differs/);
const resources = Object.entries(recovery.Resources);
const resourceOf = (type, predicate = () => true) => {
  const found = resources.find(([, value]) => value.Type === type && predicate(value));
  assert.ok(found, `Missing ${type} recovery resource`);
  return found[1];
};
const values = (value) => JSON.stringify(value ?? null);
const recoveryTask = resourceOf('AWS::ECS::TaskDefinition', value => value.Properties?.Family?.includes('recovery-drill'));
const apiTask = resourceOf('AWS::ECS::TaskDefinition', value => value.Properties?.Family?.includes('recovery-api'));
const copyTask = resourceOf('AWS::ECS::TaskDefinition', value => value.Properties?.Family?.includes('recovery-copy'));
const bootstrapTask = resourceOf('AWS::ECS::TaskDefinition', value => value.Properties?.Family?.includes('index-bootstrap'));
const bootstrapRole = resourceOf('AWS::IAM::Role', value => values(value.Properties?.RoleName).includes('index-bootstrap'));
const purgeRole = resourceOf('AWS::IAM::Role', value => values(value.Properties?.RoleName).includes('purge-executor'));
const roleLogicalId = role => Object.entries(recovery.Resources).find(([, value]) => value === role)?.[0];
const policiesForRole = role => resources
  .filter(([, value]) => value.Type === 'AWS::IAM::Policy' && values(value.Properties.Roles).includes(roleLogicalId(role)))
  .map(([, value]) => value.Properties.PolicyDocument);
const retentionCopyRole = resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Recovery-side copy identity'));
const retentionStatements = policiesForRole(retentionCopyRole).flatMap(policy => policy.Statement)
  .filter(statement => [statement.Action].flat().includes('s3:PutObjectRetention'));
assert.equal(retentionStatements.length, 1, 'Only the reviewed exact media retention statement is permitted.');
assert.deepEqual([retentionStatements[0].Action].flat(), ['s3:PutObjectRetention']);
assert.ok(values(retentionStatements[0].Resource).includes('/payload/sha256/*'));
const retentionProtectedBucketId = resources.find(([, value]) => value.Type === 'AWS::S3::Bucket' &&
  value.Properties?.ObjectLockConfiguration?.Rule?.DefaultRetention?.Mode === 'GOVERNANCE')?.[0];
assert.ok(retentionProtectedBucketId && values(retentionStatements[0].Resource).includes(retentionProtectedBucketId));
assert.ok(!values(retentionStatements[0].Resource).includes('/snapshots/') && !values(retentionStatements[0].Resource).includes('/evidence/'));
assert.deepEqual(retentionStatements[0].Condition, {
  StringEquals: { 's3:object-lock-mode': 'GOVERNANCE' },
  NumericGreaterThanEquals: { 's3:object-lock-remaining-retention-days': '90' },
  NumericLessThanEquals: { 's3:object-lock-remaining-retention-days': '92' },
});
for (const [, resource] of resources.filter(([, value]) => value.Type === 'AWS::IAM::Policy')) {
  assert.ok(!values(resource.Properties.PolicyDocument).includes('s3:BypassGovernanceRetention'));
  if (resource.Properties.PolicyDocument.Statement.some(statement => [statement.Action].flat().includes('s3:PutObjectRetention') &&
    values(statement.Resource).includes(retentionProtectedBucketId)))
    assert.ok(values(resource.Properties.Roles).includes(roleLogicalId(retentionCopyRole)), 'No other role receives retention changes.');
}
const captureTask = Object.entries(foundation.Resources).find(([, value]) => value.Type === 'AWS::ECS::TaskDefinition' && value.Properties?.Family?.includes('recovery-capture'))?.[1];
assert.ok(captureTask, 'Missing primary read-only capture task');
const fenceTable = resourceOf('AWS::DynamoDB::Table', value => values(value.Properties?.TableName).includes('recovery-fence'));
const intentTable = resourceOf('AWS::DynamoDB::Table', value => values(value.Properties?.TableName).includes('recovery-intent'));
const controlTable = resourceOf('AWS::DynamoDB::Table', value => values(value.Properties?.TableName).includes('recovery-control'));
const bucket = resourceOf('AWS::S3::Bucket', value => value.Properties?.ObjectLockConfiguration?.ObjectLockEnabled === 'Enabled');
const evidenceBucket = resourceOf('AWS::S3::Bucket', value => value.Properties?.ObjectLockConfiguration?.Rule?.DefaultRetention?.Mode === 'COMPLIANCE');
const liveBucket = resourceOf('AWS::S3::Bucket', value => value.Properties?.LifecycleConfiguration?.Rules?.some(rule => rule.Prefix === 'staging/'));
const primaryTaskEnvironment = family => {
  const task = Object.entries(foundation.Resources).find(([, value]) => value.Type === 'AWS::ECS::TaskDefinition' &&
    values(value.Properties?.Family).includes(family))?.[1];
  assert.ok(task, `Missing primary ${family} task`);
  return Object.fromEntries(task.Properties.ContainerDefinitions[0].Environment.map(({ Name, Value }) => [Name, Value]));
};

assert.equal(bucket.Properties.ObjectLockConfiguration.ObjectLockEnabled, 'Enabled');
assert.equal(bucket.Properties.ObjectLockConfiguration.Rule.DefaultRetention.Days, 90);
assert.equal(bucket.Properties.LifecycleConfiguration.Rules.length, 1);
assert.equal(bucket.Properties.LifecycleConfiguration.Rules[0].Prefix, 'payload/sha256/');
assert.equal(bucket.Properties.LifecycleConfiguration.Rules[0].Transitions[0].StorageClass, 'GLACIER_IR');
assert.equal(bucket.Properties.LifecycleConfiguration.Rules[0].Transitions[0].TransitionInDays, 90);
assert.equal(bucket.Properties.LifecycleConfiguration.Rules[0].ExpirationInDays, undefined, 'The last verified copy must not expire automatically.');
assert.equal(evidenceBucket.Properties.VersioningConfiguration.Status, 'Enabled');
assert.equal(evidenceBucket.Properties.ObjectLockConfiguration.ObjectLockEnabled, 'Enabled');
assert.equal(evidenceBucket.Properties.ObjectLockConfiguration.Rule.DefaultRetention.Mode, 'COMPLIANCE');
assert.equal(evidenceBucket.Properties.ObjectLockConfiguration.Rule.DefaultRetention.Days, 90);
assert.equal(bucket.Properties.BucketEncryption.ServerSideEncryptionConfiguration[0].BucketKeyEnabled, false,
  'Payload decryption policies depend on per-object S3 encryption context prefixes.');
assert.equal(evidenceBucket.Properties.BucketEncryption.ServerSideEncryptionConfiguration[0].BucketKeyEnabled, false,
  'Evidence decryption policies depend on per-object S3 encryption context prefixes.');
assert.equal(evidenceBucket.Properties.LifecycleConfiguration, undefined, 'Payload-free recovery evidence must not expire automatically.');
assert.equal(evidenceBucket.Properties.PublicAccessBlockConfiguration.BlockPublicAcls, true);
assert.equal(evidenceBucket.Properties.PublicAccessBlockConfiguration.IgnorePublicAcls, true);
assert.equal(evidenceBucket.Properties.PublicAccessBlockConfiguration.BlockPublicPolicy, true);
assert.equal(evidenceBucket.Properties.PublicAccessBlockConfiguration.RestrictPublicBuckets, true);
assert.ok(liveBucket, 'The isolated mutable materialization bucket must be distinct from the protected backup bucket.');
assert.notEqual(evidenceBucket, bucket, 'Payload bytes and immutable payload-free evidence must use separate buckets.');

const copyEnvironment = Object.fromEntries(copyTask.Properties.ContainerDefinitions[0].Environment.map(({ Name, Value }) => [Name, Value]));
const drillEnvironment = Object.fromEntries(recoveryTask.Properties.ContainerDefinitions[0].Environment.map(({ Name, Value }) => [Name, Value]));
const apiEnvironment = Object.fromEntries(apiTask.Properties.ContainerDefinitions[0].Environment.map(({ Name, Value }) => [Name, Value]));
const bootstrapContainer = bootstrapTask.Properties.ContainerDefinitions[0];
const bootstrapEnvironment = Object.fromEntries(bootstrapContainer.Environment.map(({ Name, Value }) => [Name, Value]));
assert.deepEqual(bootstrapContainer.Command, ['bootstrap-index']);
assert.equal(bootstrapEnvironment.Recovery__WorkerId, 'bootstrap-index');
assert.match(bootstrapEnvironment.Recovery__RecoveryWorkerBuildDigest ?? '', /^[a-f0-9]{64}$/);
assert.match(bootstrapEnvironment.Recovery__DestinationAccountId ?? '', /^\d{12}$/);
assert.equal(bootstrapEnvironment.Recovery__DestinationRegion, 'ap-southeast-2');
assert.ok(bootstrapEnvironment.Recovery__ControlTableName);
assert.deepEqual(bootstrapTask.Properties.TaskRoleArn, { 'Fn::GetAtt': [roleLogicalId(bootstrapRole), 'Arn'] });
assert.equal(bootstrapTask.Properties.NetworkMode, 'awsvpc');
assert.equal(bootstrapTask.Properties.RequiresCompatibilities[0], 'FARGATE');
assert.equal(copyEnvironment.Recovery__CaptureIntervalMinutes, '15');
assert.ok(values(copyEnvironment.Recovery__EvidenceBucket).includes(Object.entries(recovery.Resources).find(([, value]) => value === evidenceBucket)?.[0]));
assert.ok(values(drillEnvironment.Recovery__EvidenceBucket).includes(Object.entries(recovery.Resources).find(([, value]) => value === evidenceBucket)?.[0]));
assert.ok(copyEnvironment.Recovery__EvidenceKmsKeyId);
assert.ok(drillEnvironment.Recovery__EvidenceKmsKeyId);
assert.equal(drillEnvironment.Recovery__RecoveryOnly, 'true');
assert.ok(drillEnvironment.Recovery__TargetKmsKeyId);
assert.match(drillEnvironment.Recovery__ApiImageDigest, /^[0-9a-f]{64}$/);
assert.equal(drillEnvironment.Recovery__SourceRegion, 'ap-southeast-1');
assert.ok(drillEnvironment.Recovery__SourceBucket);
for (const environment of [copyEnvironment, drillEnvironment]) {
  for (const name of ['Recovery__ApiBuildDigest', 'Recovery__IngestWorkerBuildDigest', 'Recovery__RecoveryWorkerBuildDigest',
    'Recovery__RendererBuildDigest', 'Recovery__MigrationDigest',
    'Recovery__ConfigurationDigest', 'Recovery__DatabaseRootCertificateSha256']) {
    assert.match(environment[name] ?? '', /^[a-f0-9]{64}$/, `${name} must be deployment-pinned`);
  }
  assert.equal(environment.Recovery__SourceRevision?.length, 40);
  assert.deepEqual(environment.Recovery__RecoveryTemplateDigest, { Ref: 'RecoveryTemplateDigest' });
  assert.equal(environment.Recovery__LibraryId, '00000000-0000-0000-0000-000000000011');
  assert.ok(environment.Recovery__OwnerUserPoolId);
  assert.ok(environment.Recovery__OwnerUserPoolClientId);
  assert.ok(values(environment.Recovery__OwnerUserPoolIssuer).includes('https://cognito-idp.ap-southeast-2.amazonaws.com/'));
}
assert.ok(drillEnvironment.Recovery__DatabaseRootCertificate?.includes('BEGIN CERTIFICATE'));
assert.deepEqual(drillEnvironment.Recovery__OwnerApprovalTrust__KeyId, drillEnvironment.Recovery__OwnerApprovalKeyId);
assert.match(drillEnvironment.Recovery__OwnerApprovalTrust__PublicKeySha256, /^[a-f0-9]{64}$/);
assert.deepEqual(drillEnvironment.Recovery__TargetBucket, apiEnvironment.Ingest__Bucket);
assert.equal(apiEnvironment.Recovery__Enabled, 'true');
assert.ok(values(apiEnvironment.Authentication__CognitoAuthority).includes('cognito-idp.ap-southeast-2.amazonaws.com'));
assert.ok(apiTask.Properties.NetworkMode === 'awsvpc');
assert.ok(!values(recoveryTask.Properties.ContainerDefinitions[0].Environment).includes('Recovery__PrimaryReadRoleArn'), 'Recovery-only drill configuration must not carry a primary read-role ARN.');
assert.ok(!values(apiTask.Properties.ContainerDefinitions[0].Environment).includes('Recovery__PrimaryReadRoleArn'), 'Recovery API configuration must not carry a primary read-role ARN.');
for (const name of ['Recovery__ApiBuildDigest', 'Recovery__IngestWorkerBuildDigest', 'Recovery__RecoveryWorkerBuildDigest',
  'Recovery__RendererBuildDigest', 'Recovery__MigrationDigest',
  'Recovery__ConfigurationDigest', 'Recovery__DatabaseRootCertificateSha256']) {
  assert.match(apiEnvironment[name] ?? '', /^[a-f0-9]{64}$/, `Recovery API ${name} must be deployment-pinned`);
}
const captureEnvironment = Object.fromEntries(captureTask.Properties.ContainerDefinitions[0].Environment.map(({ Name, Value }) => [Name, Value]));
assert.deepEqual(apiEnvironment.Recovery__RecoveryTemplateDigest, { Ref: 'RecoveryTemplateDigest' });
assert.equal(recovery.Parameters.RecoveryTemplateDigest.Type, 'String');
assert.equal(recovery.Parameters.RecoveryTemplateDigest.AllowedPattern, '^[a-f0-9]{64}$');
assert.equal(Object.hasOwn(recovery.Parameters.RecoveryTemplateDigest, 'Default'), false);
assert.equal(captureEnvironment.Recovery__LibraryId, '00000000-0000-0000-0000-000000000011');
const canonicalLibraryId = captureEnvironment.Recovery__LibraryId;
for (const family of ['platform-nonprod-api', 'platform-nonprod-ingest-worker', 'platform-nonprod-render-worker', 'platform-nonprod-ingest-migration']) {
  assert.equal(primaryTaskEnvironment(family).Recovery__LibraryId, canonicalLibraryId,
    `${family} must use the same canonical recovery library as capture`);
}
assert.equal(primaryTaskEnvironment('platform-nonprod-api').Gallery__LibraryId, canonicalLibraryId,
  'The API gallery must use the canonical recovery library');
assert.equal(captureEnvironment.Recovery__Database__Name, 'platform');
assert.ok(captureEnvironment.Recovery__Database__Host);
assert.ok(captureEnvironment.Recovery__Database__Port);
assert.ok(captureEnvironment.Recovery__DatabaseRootCertificate?.includes('BEGIN CERTIFICATE'));
assert.match(captureEnvironment.Recovery__DatabaseRootCertificateSha256 ?? '', /^[a-f0-9]{64}$/);
assert.match(captureEnvironment.Recovery__RecoveryWorkerBuildDigest ?? '', /^[a-f0-9]{64}$/);

for (const [table, partitionKey, sortKey] of [[fenceTable, 'scopeId', 'recordId'], [intentTable, 'scopeId', 'recordId']]) {
  assert.deepEqual(table.Properties.KeySchema, [
    { AttributeName: partitionKey, KeyType: 'HASH' },
    { AttributeName: sortKey, KeyType: 'RANGE' },
  ]);
  assert.equal(table.Properties.PointInTimeRecoverySpecification.PointInTimeRecoveryEnabled, true);
  assert.equal(table.Properties.DeletionProtectionEnabled, true);
  assert.equal(table.DeletionPolicy, 'Retain');
}
for (const task of [copyTask, recoveryTask, apiTask]) {
  const environment = values(task.Properties.ContainerDefinitions[0].Environment);
  assert.ok(environment.includes('Recovery__FenceTableName'), 'Every recovery writer/reader must receive the independently provisioned fence table.');
  assert.ok(environment.includes('Recovery__IntentTableName'), 'Every recovery writer/reader must receive the independently provisioned intent table.');
}
const tablePolicies = resources
  .filter(([, value]) => value.Type === 'AWS::IAM::Policy')
  .map(([, value]) => value.Properties.PolicyDocument);
const fencePolicies = tablePolicies.flatMap(policy => policy.Statement)
  .filter(statement => values(statement.Resource).includes(Object.entries(recovery.Resources).find(([, value]) => value === fenceTable)?.[0]));
const intentPolicies = tablePolicies.flatMap(policy => policy.Statement)
  .filter(statement => values(statement.Resource).includes(Object.entries(recovery.Resources).find(([, value]) => value === intentTable)?.[0]));
assert.ok(fencePolicies.some(statement => values(statement.Action).includes('dynamodb:GetItem') && values(statement.Action).includes('dynamodb:ConditionCheckItem')));
const protectedFenceTableId = Object.entries(recovery.Resources).find(([, value]) => value === fenceTable)?.[0];
for (const role of [resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Recovery-side copy identity')),
  resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Verify and drill using recovery resources only')),
  resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('isolated restored content bucket'))]) {
  const statements = policiesForRole(role).flatMap(policy => policy.Statement)
    .filter(statement => values(statement.Resource).includes(protectedFenceTableId));
  assert.ok(statements.every(statement => !values(statement.Action).match(/dynamodb:(?:PutItem|UpdateItem|DeleteItem)/)),
    'Copy, drill, and API roles must not write or delete purge fences.');
}
assert.ok(intentPolicies.some(statement => values(statement.Action).includes('dynamodb:PutItem') && values(statement.Action).includes('dynamodb:UpdateItem')));
const controlTableId = Object.entries(recovery.Resources).find(([, value]) => value === controlTable)?.[0];
const controlPolicies = tablePolicies.flatMap(policy => policy.Statement)
  .filter(statement => values(statement.Resource).includes(controlTableId));
assert.ok(controlPolicies.some(statement => values(statement.Action).includes('dynamodb:Scan')), 'The recovery-only verifier must be able to enumerate bounded copied points.');
assert.ok(!policiesForRole(resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Recovery-side copy identity')))
  .some(policy => values(policy).includes('dynamodb:Scan')), 'The copy task must not receive the broad bootstrap inventory scan.');
for (const role of [resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Recovery-side copy identity')),
  resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Verify and drill using recovery resources only'))]) {
  const permissions = values(policiesForRole(role));
  assert.ok(permissions.includes('payload/sha256/*') && permissions.includes('snapshots/*'));
  assert.ok(!permissions.includes('payload/*'), 'Protected object permissions must stay on the exact payload namespace.');
}
const bootstrapPolicies = policiesForRole(bootstrapRole);
assert.ok(bootstrapPolicies.length > 0);
assert.ok(bootstrapPolicies.every(policy => policy.Statement.every(statement =>
  values(statement.Action).match(/dynamodb:(?:Scan|GetItem|PutItem|UpdateItem)/) &&
  values(statement.Resource).includes(controlTableId))),
'The one-time bootstrap role is limited to inventory/index operations on the recovery ControlTable.');
assert.ok(!bootstrapPolicies.some(policy => values(policy).match(/s3:|kms:|secretsmanager:|fence|intent|sts:AssumeRole/)));

const purgePolicies = policiesForRole(purgeRole);
const drillRole = resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Verify and drill using recovery resources only'));
for (const role of [drillRole, resourceOf('AWS::IAM::Role', value => values(value.Properties.Description).includes('Recovery-side copy identity'))]) {
  const retentionReads = policiesForRole(role).flatMap(policy => policy.Statement)
    .filter(statement => values(statement.Action).includes('s3:GetObjectRetention'));
  assert.equal(retentionReads.length, 1);
  assert.equal(retentionReads[0].Resource.length, 4);
  assert.deepEqual(retentionReads[0].Action, 's3:GetObjectRetention');
  for (const prefix of ['payload/sha256/*', 'snapshots/*', 'points/*', 'evidence/*'])
    assert.ok(values(retentionReads[0].Resource).includes(prefix));
}
const drillIntentWrites = policiesForRole(drillRole).flatMap(policy => policy.Statement)
  .filter(statement => values(statement.Resource).includes(Object.entries(recovery.Resources).find(([, value]) => value === intentTable)?.[0]))
  .filter(statement => values(statement.Action).includes('dynamodb:PutItem') || values(statement.Action).includes('dynamodb:UpdateItem'));
assert.equal(drillIntentWrites.length, 1);
assert.deepEqual(drillIntentWrites[0].Action, ['dynamodb:PutItem', 'dynamodb:UpdateItem']);
assert.deepEqual(drillIntentWrites[0].Condition, { 'ForAllValues:StringLike': { 'dynamodb:LeadingKeys': ['MEDIA#*'] } });
const purgeStatements = purgePolicies.flatMap(policy => policy.Statement);
const purgeTrustStatement = purgeRole.Properties.AssumeRolePolicyDocument.Statement[0];
assert.equal(purgeTrustStatement.Condition.Bool['aws:MultiFactorAuthPresent'], 'true');
assert.ok(values(purgeTrustStatement.Principal).includes('platform-recovery-purge-operator'));
const controlId = Object.entries(recovery.Resources).find(([, value]) => value === controlTable)?.[0];
const intentId = Object.entries(recovery.Resources).find(([, value]) => value === intentTable)?.[0];
const purgeControl = purgeStatements.filter(statement => values(statement.Resource).includes(controlId));
assert.ok(purgeControl.some(statement => values(statement.Action).includes('dynamodb:Query') && values(statement.Condition).includes('OWNER#*')));
assert.ok(purgeControl.some(statement => values(statement.Action).includes('dynamodb:GetItem') && values(statement.Condition).includes('POINT#*')));
assert.ok(purgeControl.every(statement => !values(statement.Action).includes('dynamodb:Scan')));
const purgeIntents = purgeStatements.filter(statement => values(statement.Resource).includes(intentId));
assert.ok(purgeIntents.length > 0 && purgeIntents.every(statement =>
  ['dynamodb:GetItem', 'dynamodb:Query'].includes(statement.Action) ||
  Array.isArray(statement.Action) && statement.Action.every(action => ['dynamodb:GetItem', 'dynamodb:Query'].includes(action))));
const purgeFences = purgeStatements.filter(statement => values(statement.Resource).includes(protectedFenceTableId));
const fenceDeletes = purgeFences.filter(statement => values(statement.Action).includes('dynamodb:DeleteItem'));
assert.equal(fenceDeletes.length, 1);
assert.ok(values(fenceDeletes[0].Condition).includes('SNAPSHOT#*'));
assert.ok(!values(fenceDeletes[0].Condition).includes('MEDIA#*'), 'Permanent media fences must never be deletable by the executor.');
assert.ok(purgeFences.some(statement => values(statement.Action).includes('dynamodb:PutItem') && values(statement.Condition).includes('MEDIA#*')));
const payloadDelete = purgeStatements.find(statement => values(statement.Action).includes('s3:DeleteObjectVersion'));
assert.ok(payloadDelete);
assert.ok(values(payloadDelete.Resource).includes('payload/sha256/*'));
assert.ok(values(payloadDelete.Resource).includes('snapshots/*'));
assert.ok(!values(payloadDelete.Resource).includes('points/*'));
assert.ok(!values(purgeStatements).match(/"s3:DeleteObject"/), 'The executor can delete exact versions only, not create delete markers.');
const versionDeletes = purgeStatements.filter(statement => values(statement.Action).includes('s3:DeleteObjectVersion'));
assert.ok(versionDeletes.every(statement => values(statement.Resource).includes('payload/sha256/*') &&
  values(statement.Resource).includes('snapshots/*') && !values(statement.Resource).includes('points/*')));
assert.ok(purgeStatements.some(statement => values(statement.Action).includes('s3:GetObjectVersion') && values(statement.Resource).includes('points/*')));
assert.ok(!values(purgePolicies).includes('evidence/drills/'));
const purgeDecrypt = purgeStatements.filter(statement => values(statement.Action).includes('kms:Decrypt'));
assert.equal(purgeDecrypt.length, 2);
assert.ok(purgeDecrypt.every(statement => values(statement.Condition).includes('s3.ap-southeast-2.amazonaws.com') &&
  values(statement.Condition).includes('kms:CallerAccount') && values(statement.Condition).includes('kms:EncryptionContext:aws:s3:arn')));
assert.ok(purgeDecrypt.some(statement => values(statement.Condition).includes('payload/sha256/*') && values(statement.Condition).includes('snapshots/*')));
assert.ok(purgeDecrypt.some(statement => values(statement.Condition).includes('points/*')));
assert.ok(!values(purgePolicies).match(/kms:(?:Sign|Verify|GetPublicKey)|secretsmanager:|sts:AssumeRole/));

const policies = resources.filter(([, value]) => value.Type === 'AWS::IAM::Policy').map(([, value]) => value.Properties.PolicyDocument);
const apiRole = resourceOf('AWS::IAM::Role', value => JSON.stringify(value.Properties.AssumeRolePolicyDocument).includes('ecs-tasks.amazonaws.com') && value.Properties.Description?.includes('isolated restored content bucket'));
const apiRoleLogicalId = Object.entries(recovery.Resources).find(([, value]) => value === apiRole)?.[0];
assert.ok(apiRoleLogicalId);
const oidcRole = resourceOf('AWS::IAM::Role', value => values(value.Properties.RoleName).includes('github-drill'));
const oidcRoleLogicalId = Object.entries(recovery.Resources).find(([, value]) => value === oidcRole)?.[0];
assert.ok(oidcRoleLogicalId);
const apiPolicyDocuments = resources
  .filter(([, value]) => value.Type === 'AWS::IAM::Policy' && values(value.Properties.Roles).includes(apiRoleLogicalId))
  .map(([, value]) => value.Properties.PolicyDocument);
assert.equal(policies.length > 0, true);
const protectedBucketId = Object.entries(recovery.Resources).find(([, value]) => value === bucket)?.[0];
assert.ok(protectedBucketId);
assert.equal(apiPolicyDocuments.some(policy => values(policy).includes(protectedBucketId)), false, 'The recovery API must not access protected recovery copies.');

const evidenceBucketId = Object.entries(recovery.Resources).find(([, value]) => value === evidenceBucket)?.[0];
assert.ok(evidenceBucketId);
const evidencePolicies = policies.filter(policy => values(policy).includes(evidenceBucketId));
assert.ok(evidencePolicies.some(policy => values(policy).includes('s3:PutObject') && values(policy).includes('points/*')));
assert.ok(evidencePolicies.some(policy => values(policy).includes('s3:PutObject') && values(policy).includes('evidence/*')));
const evidenceVersionDeletes = tablePolicies.flatMap(policy => policy.Statement)
  .filter(statement => values(statement.Resource).includes(evidenceBucketId) &&
    values(statement.Action).match(/"s3:(?:DeleteObject|DeleteObjectVersion)"/));
assert.equal(evidenceVersionDeletes.length, 0, 'No recovery identity may delete evidence objects or versions.');
assert.ok(policies.some(policy => ['s3:GetBucketLocation', 's3:GetEncryptionConfiguration', 's3:GetBucketPublicAccessBlock', 's3:GetBucketPolicyStatus', 's3:GetBucketOwnershipControls', 's3:GetBucketVersioning', 's3:GetObjectLockConfiguration'].every(action => values(policy).includes(action))));
const oidcPolicies = resources
  .filter(([, value]) => value.Type === 'AWS::IAM::Policy' && values(value.Properties.Roles).includes(oidcRoleLogicalId))
  .map(([, value]) => value.Properties.PolicyDocument);
assert.ok(oidcPolicies.some(policy => values(policy).includes(evidenceBucketId) && values(policy).includes('evidence/drills/*/recovery-qualification.v1.json') && values(policy).includes('evidence/drills/*/recovery-audit-checkpoint.v1.json') && values(policy).includes('s3:GetObjectVersion')));
assert.ok(oidcPolicies.some(policy => values(policy).includes('dynamodb:GetItem') && values(policy).includes('RUN#*')));
assert.ok(oidcPolicies.some(policy => values(policy).includes('ecs:DescribeTaskDefinition') && values(policy).includes('Resource":"*')));
assert.ok(oidcPolicies.some(policy => values(policy).includes('ec2:DescribeSubnets') && values(policy).includes('ec2:DescribeRouteTables') && values(policy).includes('ec2:DescribeSecurityGroups') && values(policy).includes('aws:RequestedRegion')));
assert.equal(oidcRole.Properties.MaxSessionDuration, 21600);
assert.ok(!oidcPolicies.some(policy => values(policy).includes(protectedBucketId)), 'The drill launcher reads qualifications only from the payload-free evidence bucket.');

assert.ok(values(oidcRole.Properties.AssumeRolePolicyDocument).includes('environment:recovery-drill'));
const apiTaskLogicalId = Object.entries(recovery.Resources).find(([, value]) => value === apiTask)?.[0];
const drillTaskLogicalId = Object.entries(recovery.Resources).find(([, value]) => value === recoveryTask)?.[0];
assert.ok(apiTaskLogicalId && drillTaskLogicalId);
const runTaskPolicies = policies.flatMap(policy => policy.Statement)
  .filter(statement => statement.Action === 'ecs:RunTask')
  .filter(statement => values(statement.Resource).includes(apiTaskLogicalId) || values(statement.Resource).includes(drillTaskLogicalId));
assert.equal(runTaskPolicies.length, 2);
for (const statement of runTaskPolicies) {
  assert.ok(values(statement.Condition).includes('ecs:subnet'));
  assert.ok(values(statement.Condition).includes('ecs:auto-assign-public-ip'));
  assert.ok(values(statement.Condition).includes('ecs:enable-execute-command'));
}

const ageAlarm = resourceOf('AWS::CloudWatch::Alarm', value => value.Properties.MetricName === 'VerifiedPointAgeSeconds');
assert.equal(ageAlarm.Properties.Threshold, 2700);
assert.equal(ageAlarm.Properties.Period, 300);
assert.equal(ageAlarm.Properties.TreatMissingData, 'breaching');
assert.ok(ageAlarm.Properties.Dimensions.some(item => item.Name === 'WorkerId' && item.Value === 'verify'));
const verifierFailureAlarm = resourceOf('AWS::CloudWatch::Alarm', value => value.Properties.MetricName === 'VerificationFailures');
assert.ok(verifierFailureAlarm.Properties.Dimensions.some(item => item.Name === 'WorkerId' && item.Value === 'verify'));
const verifierHeartbeat = resourceOf('AWS::CloudWatch::Alarm', value => value.Properties.MetricName === 'RecoveryWorkerHeartbeat' &&
  value.Properties.Dimensions.some(item => item.Name === 'WorkerId' && item.Value === 'verify'));
assert.equal(verifierHeartbeat.Properties.EvaluationPeriods * verifierHeartbeat.Properties.Period, 900);
const heartbeatAlarm = resourceOf('AWS::CloudWatch::Alarm', value => values(value.Properties).includes('RecoveryWorkerHeartbeat'));
assert.equal(heartbeatAlarm.Properties.TreatMissingData, 'breaching');

const primaryValues = values(foundation.Resources);
assert.ok(primaryValues.includes('RecoveryObservationQueue'));
assert.ok(primaryValues.includes('RecoverySourceReadRole'));
assert.ok(!primaryValues.includes('BackupAgeHours'), 'The previous placeholder backup alarm is removed.');
const schedules = resources.filter(([, value]) => value.Type === 'AWS::Events::Rule' && value.Properties.ScheduleExpression).map(([, value]) => value.Properties.ScheduleExpression);
assert.ok(schedules.includes('rate(5 minutes)'), 'Recovery copy verification and freshness polling must run every five minutes.');

await import('./verifier-publisher-policy-proposal.mjs');
await import('./verification-table-proposal.mjs');
console.log('Recovery infrastructure isolation, retention, scheduling, and alarm contracts passed.');
