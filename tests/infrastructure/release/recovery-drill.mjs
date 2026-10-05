import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import { drillConfiguration, validateDrillTask, validateIsolatedNetwork } from '../../../scripts/run-recovery-drill.mjs';
import { qualificationPointers } from '../../../scripts/collect-recovery-qualification.mjs';

const environment = {
  PRIMARY_ACCOUNT_ID: '123456789012', RECOVERY_ACCOUNT_ID: '234567890123', AWS_REGION: 'ap-southeast-2',
  RECOVERY_ROLE_ARN: 'arn:aws:iam::234567890123:role/recovery-drill',
  RECOVERY_CLUSTER_ARN: 'arn:aws:ecs:ap-southeast-2:234567890123:cluster/recovery',
  RECOVERY_TASK_DEFINITION_ARN: 'arn:aws:ecs:ap-southeast-2:234567890123:task-definition/drill:1',
  RECOVERY_CONTAINER_NAME: 'recovery', RECOVERY_WORKER_DIGEST: 'a'.repeat(64),
  RECOVERY_SUBNET_IDS: '["subnet-abc123"]', RECOVERY_SECURITY_GROUP_IDS: '["sg-abc123"]',
};
const configuration = drillConfiguration(environment);
const task = {
  taskDefinitionArn: configuration.taskDefinition, status: 'ACTIVE', networkMode: 'awsvpc', requiresCompatibilities: ['FARGATE'],
  taskRoleArn: 'arn:aws:iam::234567890123:role/recovery-worker',
  executionRoleArn: 'arn:aws:iam::234567890123:role/recovery-execution',
  containerDefinitions: [{ name: 'recovery', image: `234567890123.dkr.ecr.ap-southeast-2.amazonaws.com/recovery@sha256:${'a'.repeat(64)}`,
    environment: [{ name: 'Recovery__RunnerSubnetIds', value: 'subnet-abc123' }, { name: 'Recovery__RunnerSecurityGroupId', value: 'sg-abc123' }] }],
};
validateDrillTask(task, configuration);
const subnets = [{ SubnetId: 'subnet-abc123', OwnerId: configuration.account, VpcId: 'vpc-abc', State: 'available', MapPublicIpOnLaunch: false }];
const securityGroups = [{ GroupId: 'sg-abc123', OwnerId: configuration.account, VpcId: 'vpc-abc', IpPermissions: [] }];
const checkNetwork = (networkSubnets, networkRoutes) => validateIsolatedNetwork(networkSubnets, networkRoutes, securityGroups, configuration);
const routes = [{ OwnerId: configuration.account, VpcId: 'vpc-abc',
  Associations: [{ SubnetId: 'subnet-abc123', AssociationState: { State: 'associated' } }],
  Routes: [{ GatewayId: 'local', State: 'active', DestinationCidrBlock: '10.0.0.0/16' },
    { GatewayId: 'vpce-abc123', DestinationPrefixListId: 'pl-abc123', State: 'active' }] }];
checkNetwork(subnets, routes);
assert.throws(() => checkNetwork([{ ...subnets[0], OwnerId: environment.PRIMARY_ACCOUNT_ID }], routes));
assert.throws(() => checkNetwork(subnets, [{ ...routes[0], Associations: [] }]));
assert.throws(() => checkNetwork(subnets, [{ ...routes[0], VpcId: 'vpc-other' }]));
assert.throws(() => validateIsolatedNetwork(subnets, routes, [{ ...securityGroups[0], VpcId: 'vpc-other' }], configuration));
assert.throws(() => validateIsolatedNetwork(subnets, routes, [{ ...securityGroups[0], IpPermissions: [{ IpProtocol: '-1' }] }], configuration));
assert.throws(() => validateDrillTask(task, { ...configuration, securityGroups: ['sg-other'] }), /pinned task configuration/);
for (const route of [{ GatewayId: 'igw-abc', State: 'active', DestinationCidrBlock: '0.0.0.0/0' },
  { NatGatewayId: 'nat-abc', State: 'active' }, { VpcPeeringConnectionId: 'pcx-abc', State: 'active' },
  { TransitGatewayId: 'tgw-abc', State: 'active' }])
  assert.throws(() => checkNetwork(subnets, [{ ...routes[0], Routes: [...routes[0].Routes, route] }]));
assert.throws(() => drillConfiguration({ ...environment, RECOVERY_ACCOUNT_ID: environment.PRIMARY_ACCOUNT_ID }), /independent accounts/);
assert.throws(() => drillConfiguration({ ...environment, AWS_REGION: 'ap-southeast-1' }), /Invalid recovery drill setting/);
assert.throws(() => drillConfiguration({ ...environment, RECOVERY_SUBNET_IDS: '["subnet-abc123","subnet-abc123"]' }), /Invalid recovery drill setting/);
assert.throws(() => drillConfiguration({ ...environment, RECOVERY_TASK_DEFINITION_ARN: 'arn:aws:ecs:ap-southeast-1:123456789012:task-definition/drill:1' }), /Invalid recovery drill setting/);
assert.throws(() => validateDrillTask({ ...task, taskRoleArn: 'arn:aws:iam::123456789012:role/primary' }, configuration), /recovery account/);
for (const image of ['example/recovery:latest', `example/recovery@sha256:${'b'.repeat(64)}`,
  `123456789012.dkr.ecr.ap-southeast-1.amazonaws.com/recovery@sha256:${'a'.repeat(64)}`]) {
  assert.throws(() => validateDrillTask({ ...task, containerDefinitions: [{ name: 'recovery', image }] }, configuration), /immutable worker/);
}
for (const credential of ['Recovery__PrimaryReadRoleArn', 'Recovery__SourceDatabaseSecretArn',
  'AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_PROFILE', 'AWS_ROLE_ARN']) {
  const changed = structuredClone(task);
  changed.containerDefinitions[0].secrets = [{ name: credential, valueFrom: 'forbidden-primary-secret' }];
  assert.throws(() => validateDrillTask(changed, configuration), /primary reader credentials/);
}
const environmentCredential = structuredClone(task);
environmentCredential.containerDefinitions[0].environment = [{ name: 'AWS_SESSION_TOKEN', value: 'forbidden' }];
assert.throws(() => validateDrillTask(environmentCredential, configuration), /primary reader credentials/);
assert.throws(() => drillConfiguration({ ...environment,
  RECOVERY_SECURITY_GROUP_IDS: JSON.stringify(Array.from({ length: 6 }, (_, index) => `sg-abc${index}`)) }), /Invalid recovery drill setting/);
const workflow = readFileSync('.github/workflows/recovery-drill.yml', 'utf8');
assert.ok(workflow.includes("cron: '0 2 1 1,4,7,10 *'"));
assert.ok(workflow.includes('workflow_dispatch:') && !workflow.includes('pull_request'));
assert.ok(workflow.includes("github.repository == 'FSN-Private/software_construction'"));
assert.ok(workflow.includes("github.ref == 'refs/heads/master'"));
assert.ok(workflow.includes("vars.RECOVERY_DRILL_ENABLED == 'true'"));
assert.ok(workflow.includes('environment: recovery-drill') && workflow.includes('id-token: write'));
assert.ok(workflow.includes('allowed-account-ids: ${{ vars.RECOVERY_ACCOUNT_ID }}'));
assert.ok(workflow.includes('node scripts/run-recovery-drill.mjs'));
assert.ok(!/run:.*(?:promote|purge|deploy)/.test(workflow));
for (const pin of workflow.matchAll(/uses: ([^\s]+)/g)) assert.match(pin[1], /^[^@]+@[a-f0-9]{40}$/);
const runId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
const bucket = 'recovery-evidence';
const record = { state: { S: 'qualified' }, runId: { S: runId } };
for (const [prefix, filename] of [['qualification', 'recovery-qualification.v1.json'], ['checkpoint', 'recovery-audit-checkpoint.v1.json']]) {
  record[`${prefix}Bucket`] = { S: bucket };
  record[`${prefix}Key`] = { S: `evidence/drills/${runId}/${filename}` };
  record[`${prefix}VersionId`] = { S: 'exact-version' };
  record[`${prefix}Sha256`] = { S: 'a'.repeat(64) };
  record[`${prefix}ByteLength`] = { N: '1024' };
}
assert.equal(qualificationPointers(record, runId, bucket).length, 2);
for (const change of [{ state: { S: 'pending' } }, { runId: { S: 'foreign' } },
  { qualificationBucket: { S: 'primary-bucket' } }, { qualificationKey: { S: 'points/private-manifest.json' } },
  { qualificationVersionId: { S: 'null' } }, { checkpointSha256: { S: 'bad' } },
  { qualificationByteLength: { N: '1048577' } }])
  assert.throws(() => qualificationPointers({ ...record, ...change }, runId, bucket));
const exportWorkflow = readFileSync('.github/workflows/recovery-qualification.yml', 'utf8');
assert.ok(exportWorkflow.includes('environment: recovery-drill') && exportWorkflow.includes("github.ref == 'refs/heads/master'"));
assert.ok(exportWorkflow.includes('collect-recovery-qualification.mjs'));
assert.ok(exportWorkflow.indexOf('collect-recovery-qualification.mjs') < exportWorkflow.indexOf('uses: actions/upload-artifact@'));
assert.ok(exportWorkflow.includes('name: recovery-qualification') && !exportWorkflow.includes('path: artifacts/recovery-worker'));
assert.ok(exportWorkflow.includes('secrets.RECOVERY_OFFLINE_AUDIT_ANCHOR'));
for (const pin of exportWorkflow.matchAll(/uses: ([^\s]+)/g)) assert.match(pin[1], /^[^@]+@[a-f0-9]{40}$/);
console.log('Recovery drill rejects foreign accounts, mutable images and primary credentials; protected workflow contracts passed.');
