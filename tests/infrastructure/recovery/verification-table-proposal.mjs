import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const template = JSON.parse(readFileSync(
  'packages/infrastructure/cdk.out-recovery/Recovery-nonprod.template.json', 'utf8'));
const proposal = JSON.parse(readFileSync(
  'packages/infrastructure/policy-proposals/recovery-verifier-publisher.v1.json', 'utf8'));
const recoveryVerificationTables = Object.values(template.Resources ?? {}).filter(resource =>
  resource.Type === 'AWS::DynamoDB::Table' &&
  JSON.stringify(resource.Properties.TableName).includes('recovery-verification'));
assert.equal(recoveryVerificationTables.length, 0,
  'The pending VerificationTable proposal must not provision a table before approval.');
assert.equal(proposal.status, 'proposal-only');
assert.equal(proposal.deployable, false);
assert.equal(proposal.attachedToCdk, false);
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
  roleAttached: false,
}]);
console.log('Recovery verification table remains a review-only proposal and is not synthesized.');
