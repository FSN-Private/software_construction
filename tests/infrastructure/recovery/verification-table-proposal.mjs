import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const template = JSON.parse(readFileSync(
  'packages/infrastructure/cdk.out-recovery/Recovery-nonprod.template.json', 'utf8'));
const proposal = JSON.parse(readFileSync(
  'packages/infrastructure/policy-proposals/recovery-verifier-publisher.v1.json', 'utf8'));
const recoveryVerificationTables = Object.values(template.Resources ?? {}).filter(resource =>
  resource.Type === 'AWS::DynamoDB::Table' &&
  JSON.stringify(resource.Properties.TableName).includes('recovery-verification'));
assert.equal(recoveryVerificationTables.length, 1,
  'The specifically approved VerificationTable must be synthesized once.');
const table = recoveryVerificationTables[0];
assert.equal(table.DeletionPolicy, 'Retain');
assert.equal(table.UpdateReplacePolicy, 'Retain');
assert.equal(table.Properties.DeletionProtectionEnabled, true);
assert.equal(table.Properties.PointInTimeRecoverySpecification.PointInTimeRecoveryEnabled, true);
assert.equal(table.Properties.SSESpecification.SSEEnabled, true);
assert.equal(table.Properties.SSESpecification.SSEType, 'KMS');
assert.equal(table.Properties.BillingMode, 'PAY_PER_REQUEST');
assert.deepEqual(table.Properties.KeySchema, [{ AttributeName: 'pk', KeyType: 'HASH' }, { AttributeName: 'sk', KeyType: 'RANGE' }]);
assert.equal(proposal.status, 'approved-local-definition');
assert.equal(proposal.deployable, false);
assert.equal(proposal.attachedToCdk, true);
assert.deepEqual(proposal.proposedResources, [{
  constructId: 'RecoveryVerificationTable',
  tableName: 'platform-__ENVIRONMENT__-recovery-verification',
  partitionKey: 'pk',
  sortKey: 'sk',
  billingMode: 'PAY_PER_REQUEST',
  encryption: 'existing recovery customer-managed DynamoDB key',
  pointInTimeRecovery: true,
  deletionProtection: true,
  removalPolicy: 'RETAIN',
  roleAttached: true,
}]);
console.log('Approved recovery verification table synthesis preserves retention, encryption and recovery protections.');
