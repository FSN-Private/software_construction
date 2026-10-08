import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const proposal = JSON.parse(readFileSync(
  'packages/infrastructure/policy-proposals/recovery-verifier-publisher.v1.json', 'utf8'));
const statements = proposal.policyDocument.Statement;
const bySid = sid => {
  const statement = statements.find(candidate => candidate.Sid === sid);
  assert.ok(statement, `Missing policy statement ${sid}`);
  return statement;
};
const values = value => Array.isArray(value) ? value : [value];
const actions = statements.flatMap(statement => values(statement.Action));
const taskEnvironment = proposal.requiredTaskEnvironment;

assert.equal(proposal.status, 'approved-local-definition');
assert.equal(proposal.deployable, false);
assert.equal(proposal.attachedToCdk, true);
assert.ok(proposal.scopeBindings.ownerScopePartitionKey.startsWith('OWNER#__'));
assert.match(proposal.scopeBindings.scopeHashDefinition, /SHA256\(UTF8\(ownerId \+ NUL \+ libraryId\)\)/);
assert.deepEqual(proposal.task.command, ['verify']);
assert.equal(proposal.task.schedule, 'rate(15 minutes)');

const requiredEnvironment = [
  'Recovery__Environment', 'Recovery__WorkerId', 'Recovery__OwnerId', 'Recovery__LibraryId',
  'Recovery__SourceRevision', 'Recovery__ApiBuildDigest', 'Recovery__IngestWorkerBuildDigest',
  'Recovery__RecoveryWorkerBuildDigest', 'Recovery__RendererBuildDigest', 'Recovery__RecoveryTemplateDigest',
  'Recovery__MigrationDigest', 'Recovery__ConfigurationDigest', 'Recovery__DatabaseRootCertificate',
  'Recovery__DatabaseRootCertificateSha256', 'Recovery__DatabaseSecretArn', 'Recovery__Database__Host',
  'Recovery__Database__Port', 'Recovery__Database__Name', 'Recovery__MigrationDirectory',
  'Recovery__AuditGenesisKey', 'Recovery__AuditGenesisVersionId', 'Recovery__AuditGenesisSha256',
  'Recovery__AuditGenesisByteLength', 'Recovery__AuditMinimumSequence',
  'Recovery__AuditMinimumEventSha256', 'Recovery__ObservationQueueArn', 'Recovery__ObservationQueueUrl',
];
for (const name of requiredEnvironment) assert.ok(taskEnvironment[name], `Missing task binding ${name}`);
assert.equal(taskEnvironment.Recovery__AuditGenesisKey, 'evidence/audit/genesis.v1.json');
assert.equal(taskEnvironment.Recovery__ObservationQueueArn, '__EXACT_PRIMARY_OBSERVATION_QUEUE_ARN__');

const trust = proposal.roleTrustPolicy.Statement;
assert.equal(trust.length, 1);
assert.deepEqual(trust[0].Principal, { Service: 'ecs-tasks.amazonaws.com' });
assert.deepEqual(trust[0].Condition.StringEquals, { 'aws:SourceAccount': '__RECOVERY_ACCOUNT_ID__' });
assert.equal(trust[0].Condition.ArnLike['aws:SourceArn'], 'arn:aws:ecs:ap-southeast-2:__RECOVERY_ACCOUNT_ID__:*');

assert.deepEqual(bySid('GetRecoveryTaskIdentity').Action, 'sts:GetCallerIdentity');
assert.deepEqual(bySid('GetRecoveryTaskIdentity').Resource, '*');
assert.deepEqual(bySid('ReadOnlyRecoveryDatabaseSecret').Action, 'secretsmanager:GetSecretValue');
assert.deepEqual(bySid('ReadOnlyRecoveryDatabaseSecret').Resource, '__RECOVERY_DATABASE_SECRET_ARN__');

const ownerIndexRead = bySid('ReadConfiguredOwnerIndexHeadStronglyConsistent');
assert.equal(ownerIndexRead.Action, 'dynamodb:GetItem');
assert.deepEqual(ownerIndexRead.Condition['ForAllValues:StringEquals']['dynamodb:LeadingKeys'], [
  'OWNER#__LOWERCASE_SHA256_OF_UTF8_OWNER_ID_NUL_LIBRARY_ID__',
]);
const copyRead = bySid('ReadCopyPointerItemsFromIndexedPoints');
assert.deepEqual(copyRead.Action, 'dynamodb:GetItem');
assert.deepEqual(copyRead.Condition['ForAllValues:StringLike']['dynamodb:LeadingKeys'], ['POINT#*']);

const pointWrite = bySid('ReadWritePointVerificationOutbox');
assert.deepEqual(values(pointWrite.Action).sort(), ['dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:UpdateItem']);
assert.deepEqual(pointWrite.Resource, '__RECOVERY_VERIFICATION_TABLE_ARN__');
assert.deepEqual(pointWrite.Condition['ForAllValues:StringLike']['dynamodb:LeadingKeys'], ['POINT#*']);
assert.deepEqual(values(pointWrite.Condition['ForAllValues:StringEquals']['dynamodb:Attributes']).sort(), [
  'auditHeadEventSha256', 'auditHeadSequence', 'captureId', 'changeSequence', 'checkpointSha256', 'deliveredAtUtc',
  'fullManifestSha256', 'libraryId', 'messageBodies', 'messageCount', 'outboxSha256', 'ownerId', 'payloadSha256',
  'envelopeBytes', 'pk', 'pointId', 'proofState', 'safeClassCoverage', 'safeStatus', 'sk', 'snapshotCutoffUtc', 'sourceManifestSha256',
  'state', 'supersededAtUtc', 'verifiedAtUtc', 'workerBuildDigest',
].sort());

const ownerWrite = bySid('ReadOrAdvanceConfiguredOwnerVerificationAndAuditHead');
assert.deepEqual(values(ownerWrite.Action).sort(), ['dynamodb:GetItem', 'dynamodb:UpdateItem']);
assert.deepEqual(ownerWrite.Resource, '__RECOVERY_VERIFICATION_TABLE_ARN__');
assert.deepEqual(ownerWrite.Condition['ForAllValues:StringEquals']['dynamodb:LeadingKeys'], [
  'OWNER#__LOWERCASE_SHA256_OF_UTF8_OWNER_ID_NUL_LIBRARY_ID__',
]);
assert.ok(ownerWrite.Condition['ForAllValues:StringEquals']['dynamodb:Attributes'].includes('auditHeadSequence'));
assert.ok(ownerWrite.Condition['ForAllValues:StringEquals']['dynamodb:Attributes'].includes('auditHeadEventSha256'));

for (const sid of ['ReadConfiguredOwnerIndexHeadStronglyConsistent', 'ReadCopyPointerItemsFromIndexedPoints']) {
  assert.deepEqual(bySid(sid).Resource, '__RECOVERY_CONTROL_TABLE_ARN__');
}
for (const sid of ['ReadWritePointVerificationOutbox', 'ReadOrAdvanceConfiguredOwnerVerificationAndAuditHead']) {
  assert.deepEqual(bySid(sid).Resource, '__RECOVERY_VERIFICATION_TABLE_ARN__');
}
for (const statement of statements.filter(item => values(item.Action).some(action => action.startsWith('dynamodb:')))) {
  assert.ok(!values(statement.Action).some(action => action === 'dynamodb:Scan'));
}
const controlWrites = statements.filter(item => item.Resource === '__RECOVERY_CONTROL_TABLE_ARN__' &&
  values(item.Action).some(action => ['dynamodb:PutItem', 'dynamodb:UpdateItem', 'dynamodb:DeleteItem'].includes(action)));
assert.equal(controlWrites.length, 0, 'ControlTable remains read-only to the proposed verifier role.');
assert.ok(!actions.includes('dynamodb:Scan'));
assert.ok(!actions.includes('dynamodb:Query'));
assert.ok(!actions.includes('sts:AssumeRole'));
assert.ok(!actions.some(action => action.startsWith('s3:Delete') || action.startsWith('s3:Put')));
assert.ok(!actions.includes('sqs:ReceiveMessage'));
assert.ok(!actions.includes('kms:Verify'));
assert.ok(!actions.includes('kms:Decrypt') || statements.filter(item => item.Action === 'kms:Decrypt').every(item =>
  item.Condition?.StringEquals?.['kms:ViaService'] && item.Condition?.StringEquals?.['kms:CallerAccount']));

const signerStatements = statements.filter(item => values(item.Action).some(action =>
  action === 'kms:Sign' || action === 'kms:GetPublicKey'));
assert.ok(signerStatements.length > 0);
assert.ok(signerStatements.every(item => item.Resource === '__RECOVERY_EVIDENCE_SIGNING_KEY_ARN__'));
assert.ok(!JSON.stringify(proposal.policyDocument).includes('OWNER_APPROVAL_KEY_ARN'));
assert.ok(!JSON.stringify(proposal.policyDocument).includes('kms:GenerateDataKey'));

const payloadRead = bySid('ReadExactRecoveryPayloadVersions');
assert.deepEqual(payloadRead.Action, ['s3:GetObjectVersion', 's3:GetObjectRetention']);
assert.deepEqual(payloadRead.Resource, [
  '__RECOVERY_PAYLOAD_BUCKET_ARN__/payload/sha256/*',
  '__RECOVERY_PAYLOAD_BUCKET_ARN__/snapshots/*',
]);
const evidenceRead = bySid('ReadExactRecoveryEvidenceVersions');
assert.deepEqual(evidenceRead.Resource, [
  '__RECOVERY_EVIDENCE_BUCKET_ARN__/points/*',
  '__RECOVERY_EVIDENCE_BUCKET_ARN__/evidence/audit/genesis.v1.json',
]);
for (const sid of ['DecryptRecoveryPayloadThroughRegionalS3', 'DecryptRecoveryEvidenceThroughRegionalS3']) {
  const statement = bySid(sid);
  assert.deepEqual(statement.Action, 'kms:Decrypt');
  assert.equal(statement.Condition.StringEquals['kms:ViaService'], 's3.ap-southeast-2.amazonaws.com');
  assert.equal(statement.Condition.StringEquals['kms:CallerAccount'], '__RECOVERY_ACCOUNT_ID__');
  assert.ok(statement.Condition.StringLike['kms:EncryptionContext:aws:s3:arn'].length > 0);
}
const secretDecrypt = bySid('DecryptRecoveryDatabaseSecretThroughSecretsManager');
assert.equal(secretDecrypt.Condition.StringEquals['kms:ViaService'], 'secretsmanager.ap-southeast-2.amazonaws.com');
assert.equal(secretDecrypt.Condition.StringEquals['kms:EncryptionContext:SecretARN'], '__RECOVERY_DATABASE_SECRET_ARN__');

const queuePublish = bySid('PublishOnlyToPrimaryRecoveryObservationQueue');
assert.deepEqual(queuePublish.Action, 'sqs:SendMessage');
assert.deepEqual(queuePublish.Resource, '__EXACT_PRIMARY_OBSERVATION_QUEUE_ARN__');
assert.deepEqual(actions.filter(action => action.startsWith('sqs:')), ['sqs:SendMessage']);

const stackSource = readFileSync('packages/infrastructure/src/recovery/recovery-stack.ts', 'utf8');
assert.ok(stackSource.includes('recovery-verifier-publisher.v1.json'), 'CDK must bind the reviewed policy document.');
assert.equal(proposal.proposedResources.length, 1);
assert.equal(proposal.proposedResources[0].constructId, 'RecoveryVerificationTable');
assert.equal(proposal.proposedResources[0].roleAttached, true);

const template = JSON.parse(readFileSync('packages/infrastructure/cdk.out-recovery/Recovery-nonprod.template.json', 'utf8'));
const resources = Object.entries(template.Resources);
const [roleId, role] = resources.find(([id, resource]) => id.startsWith('RecoveryVerifierPublisherRole') && resource.Type === 'AWS::IAM::Role');
assert.equal(role.Properties.AssumeRolePolicyDocument.Statement[0].Principal.Service, 'ecs-tasks.amazonaws.com');
assert.ok(role.Properties.AssumeRolePolicyDocument.Statement[0].Condition.StringEquals['aws:SourceAccount']);
const policy = resources.find(([, resource]) => resource.Type === 'AWS::IAM::Policy' &&
  resource.Properties.Roles.some(role => role.Ref === roleId))[1].Properties.PolicyDocument.Statement;
for (const approved of statements) {
  const actual = policy.find(statement => statement.Sid === approved.Sid);
  assert.ok(actual, `Missing synthesized statement ${approved.Sid}`);
  assert.deepEqual(actual.Action, approved.Action);
  assert.ok(!JSON.stringify(actual).includes('__'), `Unbound placeholder in ${approved.Sid}`);
}
const controlTableId = resources.find(([id, resource]) => id.startsWith('RecoveryControlTable') && resource.Type === 'AWS::DynamoDB::Table')[0];
assert.ok(policy.filter(statement => JSON.stringify(statement.Resource).includes(controlTableId))
  .every(statement => values(statement.Action).every(action => action === 'dynamodb:GetItem')));
const actualActions = policy.flatMap(statement => values(statement.Action));
assert.ok(!actualActions.some(action => /^(s3:(Put|Delete)|ecs:|iam:|sts:AssumeRole)/.test(action)));
assert.deepEqual(actualActions.filter(action => action.startsWith('sqs:')), ['sqs:SendMessage']);
assert.ok(!JSON.stringify(policy).includes('kms:GenerateDataKey'));
const runtimeSecretId = resources.find(([id, resource]) => id.startsWith('RecoveryDatabaseRuntimeCredentials') && resource.Type === 'AWS::SecretsManager::Secret')[0];
const drillRoleId = resources.find(([id, resource]) => id.startsWith('RecoveryOnlyTaskRole') && resource.Type === 'AWS::IAM::Role')[0];
const drillPolicy = resources.find(([, resource]) => resource.Type === 'AWS::IAM::Policy' &&
  resource.Properties.Roles.some(role => role.Ref === drillRoleId))[1].Properties.PolicyDocument.Statement;
const runtimeReader = drillPolicy.filter(statement => values(statement.Action).includes('secretsmanager:GetSecretValue') &&
  JSON.stringify(statement.Resource).includes(runtimeSecretId));
assert.equal(runtimeReader.length, 1);
assert.equal(runtimeReader[0].Action, 'secretsmanager:GetSecretValue');
assert.deepEqual(runtimeReader[0].Resource, { Ref: runtimeSecretId });
assert.ok(!drillPolicy.some(statement => values(statement.Action).some(action => action.startsWith('sqs:'))));
const support = JSON.parse(readFileSync('packages/infrastructure/policy-proposals/recovery-verification-reader-support.v1.json', 'utf8'));
assert.equal(support.status, 'approved-local-definition');
assert.equal(support.attachedToCdk, true);
const tableDecrypt = policy.find(statement => statement.Sid === 'DecryptOnlyRecoveryControlAndVerificationThroughDynamoDb');
assert.equal(tableDecrypt.Action, 'kms:Decrypt');
assert.equal(tableDecrypt.Condition.StringEquals['kms:ViaService'], 'dynamodb.ap-southeast-2.amazonaws.com');
const configuredRecoveryAccount = role.Properties.AssumeRolePolicyDocument.Statement[0].Condition.StringEquals['aws:SourceAccount'];
assert.equal(tableDecrypt.Condition.StringEquals['kms:CallerAccount'], configuredRecoveryAccount);
assert.equal(tableDecrypt.Condition.StringEquals['kms:EncryptionContext:aws:dynamodb:subscriberId'], configuredRecoveryAccount);
assert.deepEqual(tableDecrypt.Condition.StringEquals['kms:EncryptionContext:aws:dynamodb:tableName'], [
  { Ref: controlTableId }, { Ref: resources.find(([id, resource]) => id.startsWith('RecoveryVerificationTable') && resource.Type === 'AWS::DynamoDB::Table')[0] },
]);
const proofReader = drillPolicy.find(statement => statement.Sid === 'ReadOnlyRecoveryVerificationProofs');
assert.equal(proofReader.Action, 'dynamodb:GetItem');
assert.deepEqual(proofReader.Resource, { 'Fn::GetAtt': [resources.find(([id, resource]) => id.startsWith('RecoveryVerificationTable') && resource.Type === 'AWS::DynamoDB::Table')[0], 'Arn'] });
assert.equal(proofReader.Condition['ForAllValues:StringLike']['dynamodb:LeadingKeys'][0], 'POINT#*');
assert.match(proofReader.Condition['ForAllValues:StringLike']['dynamodb:LeadingKeys'][1], /^OWNER#[a-f0-9]{64}$/);
const verifier = resources.find(([id, resource]) => id.startsWith('RecoveryVerifierTask') && resource.Type === 'AWS::ECS::TaskDefinition')[1];
assert.deepEqual(verifier.Properties.TaskRoleArn, { 'Fn::GetAtt': [roleId, 'Arn'] });
const container = verifier.Properties.ContainerDefinitions[0];
assert.deepEqual(container.Command, ['verify']);
const environment = Object.fromEntries(container.Environment.map(item => [item.Name, item.Value]));
for (const name of requiredEnvironment) assert.ok(Object.hasOwn(environment, name), `Missing synthesized task binding ${name}`);
assert.deepEqual(environment.Recovery__RecoveryTemplateDigest, { Ref: 'RecoveryTemplateDigest' });
assert.ok(environment.Recovery__VerificationTableName);
assert.ok(!Object.hasOwn(environment, 'Recovery__PrimaryReadRoleArn'));
assert.ok(!Object.hasOwn(environment, 'Recovery__TargetBucket'));
for (const [, resource] of resources.filter(([, resource]) => resource.Type === 'AWS::ECS::TaskDefinition' && resource !== verifier)) {
  for (const other of resource.Properties.ContainerDefinitions) {
    assert.ok(!other.Environment?.some(item => item.Name === 'Recovery__ObservationQueueUrl'), 'Only the verifier may publish to primary.');
  }
}
console.log('Approved verifier role and task preserve read-only copy authority and exact publication scope.');
