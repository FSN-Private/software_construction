import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { appendFileSync, writeFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

function requireSetting(environment, name, pattern) {
  const value = environment[name];
  if (!value || (pattern && !pattern.test(value))) throw new Error(`Invalid recovery drill setting: ${name}`);
  return value;
}

export function drillConfiguration(environment = process.env) {
  const account = requireSetting(environment, 'RECOVERY_ACCOUNT_ID', /^\d{12}$/);
  const primaryAccount = requireSetting(environment, 'PRIMARY_ACCOUNT_ID', /^\d{12}$/);
  if (account === primaryAccount) throw new Error('Recovery drill requires independent accounts.');
  const region = requireSetting(environment, 'AWS_REGION', /^ap-southeast-2$/);
  const resource = (name, service) => requireSetting(environment, name,
    new RegExp(`^arn:aws:${service}:${region}:${account}:`));
  const role = requireSetting(environment, 'RECOVERY_ROLE_ARN', new RegExp(`^arn:aws:iam::${account}:role/[\\w+=,.@/-]+$`));
  const identifiers = (name, pattern, maximum) => {
    const values = JSON.parse(requireSetting(environment, name));
    if (!Array.isArray(values) || values.length === 0 || values.length > maximum ||
      new Set(values).size !== values.length || values.some((value) => typeof value !== 'string' || !pattern.test(value)))
      throw new Error(`Invalid recovery drill setting: ${name}`);
    return values;
  };
  return { account, primaryAccount, region, role,
    cluster: resource('RECOVERY_CLUSTER_ARN', 'ecs'),
    taskDefinition: resource('RECOVERY_TASK_DEFINITION_ARN', 'ecs'),
    container: requireSetting(environment, 'RECOVERY_CONTAINER_NAME', /^[\w-]{1,255}$/),
    workerDigest: requireSetting(environment, 'RECOVERY_WORKER_DIGEST', /^[a-f0-9]{64}$/),
    subnets: identifiers('RECOVERY_SUBNET_IDS', /^subnet-[a-f0-9]+$/, 16),
    securityGroups: identifiers('RECOVERY_SECURITY_GROUP_IDS', /^sg-[a-f0-9]+$/, 5) };
}

export function validateDrillTask(task, configuration) {
  if (task.taskDefinitionArn !== configuration.taskDefinition || task.status !== 'ACTIVE' ||
    task.networkMode !== 'awsvpc' || !task.requiresCompatibilities?.includes('FARGATE'))
    throw new Error('Recovery drill requires the pinned active Fargate task definition.');
  for (const role of [task.taskRoleArn, task.executionRoleArn]) {
    if (!new RegExp(`^arn:aws:iam::${configuration.account}:role/`).test(role))
      throw new Error('Recovery drill task roles must belong to the recovery account.');
  }
  const containers = task.containerDefinitions;
  if (!Array.isArray(containers) || containers.length !== 1 || containers[0].name !== configuration.container ||
    !new RegExp(`^${configuration.account}\\.dkr\\.ecr\\.${configuration.region}\\.amazonaws\\.com/[\\w./-]+@sha256:${configuration.workerDigest}$`).test(containers[0].image) || containers[0].privileged)
    throw new Error('Recovery drill requires the exact immutable worker image and container.');
  const settings = [...(containers[0].environment ?? []), ...(containers[0].secrets ?? [])];
  if (settings.some(({ name }) => /PrimaryReadRoleArn|SourceDatabaseSecretArn/.test(name) ||
    ['AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY', 'AWS_SESSION_TOKEN', 'AWS_PROFILE',
      'AWS_SHARED_CREDENTIALS_FILE', 'AWS_CONFIG_FILE', 'AWS_WEB_IDENTITY_TOKEN_FILE', 'AWS_ROLE_ARN'].includes(name)))
    throw new Error('Recovery drill must not receive primary reader credentials.');
  const pinned = new Map(settings.map(({ name, value }) => [name, value]));
  if (pinned.size !== settings.length ||
      pinned.get('Recovery__RunnerSecurityGroupId') !== configuration.securityGroups[0] || configuration.securityGroups.length !== 1 ||
      JSON.stringify((pinned.get('Recovery__RunnerSubnetIds') ?? '').split(',').sort()) !== JSON.stringify([...configuration.subnets].sort()))
    throw new Error('Recovery drill network identifiers do not match the pinned task configuration.');
}

export function validateIsolatedNetwork(subnets, routeTables, securityGroups, configuration) {
  if (!Array.isArray(subnets) || subnets.length !== configuration.subnets.length ||
    new Set(subnets.map(({ SubnetId }) => SubnetId)).size !== configuration.subnets.length ||
    subnets.some((subnet) => !configuration.subnets.includes(subnet.SubnetId) ||
      subnet.OwnerId !== configuration.account || subnet.MapPublicIpOnLaunch || subnet.State !== 'available'))
    throw new Error('Recovery drill requires private recovery-owned subnets.');
  const vpcs = new Set(subnets.map((subnet) => subnet.VpcId));
  if (vpcs.size !== 1 || !Array.isArray(routeTables) || !routeTables.length)
    throw new Error('Recovery drill network boundaries are incomplete.');
  if (!Array.isArray(securityGroups) || securityGroups.length !== configuration.securityGroups.length ||
    new Set(securityGroups.map(({ GroupId }) => GroupId)).size !== configuration.securityGroups.length ||
    securityGroups.some((group) => !configuration.securityGroups.includes(group.GroupId) ||
      group.OwnerId !== configuration.account || !vpcs.has(group.VpcId) || !Array.isArray(group.IpPermissions) || group.IpPermissions.length))
    throw new Error('Recovery drill requires its pinned recovery-owned worker security group without inbound access.');
  const associated = new Set();
  for (const table of routeTables) {
    if (!vpcs.has(table.VpcId) || table.OwnerId !== configuration.account || !table.Routes?.length)
      throw new Error('Recovery drill route table belongs to a different boundary.');
    for (const association of table.Associations ?? []) {
      if (configuration.subnets.includes(association.SubnetId) && association.AssociationState?.State === 'associated')
        associated.add(association.SubnetId);
    }
    for (const route of table.Routes) {
      const local = route.GatewayId === 'local';
      const gatewayEndpoint = /^vpce-[a-f0-9]+$/.test(route.GatewayId ?? '') && /^pl-[a-f0-9]+$/.test(route.DestinationPrefixListId ?? '');
      if (route.State !== 'active' || (!local && !gatewayEndpoint) ||
          route.NatGatewayId || route.TransitGatewayId || route.VpcPeeringConnectionId ||
          route.NetworkInterfaceId || route.InstanceId || route.EgressOnlyInternetGatewayId)
        throw new Error('Recovery drill route table permits an unapproved external path.');
    }
  }
  if (associated.size !== configuration.subnets.length)
    throw new Error('Recovery drill requires an explicit isolated route table for every subnet.');
}

export async function runDrill(environment = process.env) {
  const configuration = drillConfiguration(environment);
  const aws = (...args) => JSON.parse(execFileSync('aws', [...args, '--region', configuration.region, '--output', 'json'],
    { encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 }));
  const identity = aws('sts', 'get-caller-identity');
  const roleName = configuration.role.split('/').at(-1);
  if (identity.Account !== configuration.account || !identity.Arn?.startsWith(`arn:aws:sts::${configuration.account}:assumed-role/${roleName}/`))
    throw new Error('Recovery drill credential identity does not match its protected role.');
  const task = aws('ecs', 'describe-task-definition', '--task-definition', configuration.taskDefinition).taskDefinition;
  validateDrillTask(task, configuration);
  const subnets = aws('ec2', 'describe-subnets', '--subnet-ids', ...configuration.subnets).Subnets;
  const routes = aws('ec2', 'describe-route-tables', '--filters',
    `Name=association.subnet-id,Values=${configuration.subnets.join(',')}`).RouteTables;
  const securityGroups = aws('ec2', 'describe-security-groups', '--group-ids', ...configuration.securityGroups).SecurityGroups;
  validateIsolatedNetwork(subnets, routes, securityGroups, configuration);
  const runId = randomUUID();
  const launched = aws('ecs', 'run-task', '--cluster', configuration.cluster, '--task-definition', configuration.taskDefinition,
    '--launch-type', 'FARGATE', '--count', '1', '--disable-execute-command', '--client-token', runId,
    '--started-by', `rd-${runId.replaceAll('-', '')}`,
    '--network-configuration', JSON.stringify({ awsvpcConfiguration: {
      subnets: configuration.subnets, securityGroups: configuration.securityGroups, assignPublicIp: 'DISABLED' } }),
    '--overrides', JSON.stringify({ containerOverrides: [{ name: configuration.container, command: ['drill'],
      environment: [{ name: 'Recovery__DrillRunId', value: runId }] }] }));
  if (launched.failures?.length || launched.tasks?.length !== 1) throw new Error('Recovery drill task launch failed.');
  const taskArn = launched.tasks[0].taskArn;
  const record = { runId, taskArn, state: 'pending', launchedAtUtc: new Date().toISOString() };
  console.log(`Recovery drill started run=${runId}; qualification remains pending.`);
  writeFileSync('recovery-drill-launch.json', `${JSON.stringify(record)}\n`);
  if (environment.GITHUB_OUTPUT) appendFileSync(environment.GITHUB_OUTPUT, `run_id=${runId}\n`);
  // GitHub hosted jobs have a six-hour limit. The recovery-owned task may continue
  // through the 24-hour RTO window; a workflow timeout never becomes qualification.
  const deadline = Date.now() + 350 * 60 * 1000;
  while (Date.now() < deadline) {
    const observed = aws('ecs', 'describe-tasks', '--cluster', configuration.cluster, '--tasks', taskArn);
    if (observed.failures?.length || observed.tasks?.length !== 1) throw new Error('Recovery drill task observation failed.');
    const running = observed.tasks[0];
    if (running.lastStatus === 'STOPPED') {
      const container = running.containers?.find(({ name }) => name === configuration.container);
      record.state = container?.exitCode === 0 ? 'worker_completed' : 'worker_failed';
      record.completedAtUtc = new Date().toISOString();
      writeFileSync('recovery-drill-launch.json', `${JSON.stringify(record)}\n`);
      if (container?.exitCode !== 0) throw new Error('Recovery drill worker failed; preserve its recovery-owned evidence.');
      console.log('Recovery worker completed. Independently verify its signed qualification before accepting the drill.');
      return;
    }
    await new Promise((done) => setTimeout(done, 30_000));
  }
  throw new Error('Recovery drill remains pending outside the workflow observation window; inspect recovery-owned evidence.');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await runDrill();
