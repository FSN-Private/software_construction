import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const directory = mkdtempSync(join(tmpdir(), 'regional-infra-'));
try {
  const bundle = join(directory, 'client.zip');
  const shell = process.platform === 'win32' ? 'powershell.exe' : 'pwsh';
  const packaged = spawnSync(shell, ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', 'scripts/package-regional-acceptance.ps1', '-OutputPath', bundle], { cwd: repo, encoding: 'utf8', windowsHide: true });
  assert.equal(packaged.status, 0, packaged.stderr);
  const expectedCode = createHash('sha256').update(readFileSync(bundle)).digest('base64');
  const context = { offline: 'true', acceptanceBundle: bundle, '@aws-cdk/core:newStyleStackSynthesis': true };
  const synth = (overrides, output) => spawnSync(process.execPath, ['packages/infrastructure/dist/regional-acceptance-app.js'], {
    cwd: repo, env: { ...process.env, CDK_CONTEXT_JSON: JSON.stringify({ ...context, ...overrides }), CDK_OUTDIR: output }, encoding: 'utf8', windowsHide: true,
  });
  const output = join(directory, 'valid'), result = synth({}, output);
  assert.equal(result.status, 0, result.stderr);
  const template = JSON.parse(readFileSync(join(output, 'RegionalAcceptance-nonprod.template.json'), 'utf8'));
  const resources = Object.values(template.Resources);
  assert.ok(resources.every(item => ['AWS::Logs::LogGroup', 'AWS::IAM::Role', 'AWS::IAM::Policy', 'AWS::Lambda::Function', 'AWS::Lambda::Version', 'AWS::CDK::Metadata'].includes(item.Type)), 'Isolated stack must not construct app/data/network/events/function URL resources.');
  const only = type => { const xs = resources.filter(item => item.Type === type); assert.equal(xs.length, 1); return xs[0]; };
  const client = only('AWS::Lambda::Function').Properties;
  assert.equal(client.FunctionName, 'platform-nonprod-acceptance');
  assert.equal(client.Runtime, 'nodejs24.x'); assert.equal(client.Handler, 'handler.handler');
  assert.equal(client.MemorySize, 512); assert.equal(client.Timeout, 300); assert.equal(client.ReservedConcurrentExecutions, 1);
  assert.deepEqual(client.Architectures, ['x86_64']);
  assert.deepEqual(client.Environment.Variables, { TARGET_ORIGIN: 'https://fixtureapi123.execute-api.ap-southeast-1.amazonaws.com' });
  assert.equal(client.VpcConfig, undefined); assert.equal(client.Layers, undefined);
  const version = only('AWS::Lambda::Version');
  assert.equal(version.Properties.CodeSha256, expectedCode); assert.equal(version.DeletionPolicy, 'Retain');
  assert.equal(version.Properties.ProvisionedConcurrencyConfig, undefined);
  assert.equal(only('AWS::Logs::LogGroup').Properties.RetentionInDays, 7);
  const role = only('AWS::IAM::Role').Properties;
  assert.equal(role.ManagedPolicyArns, undefined);
  assert.equal(role.AssumeRolePolicyDocument.Statement[0].Principal.Service, 'lambda.amazonaws.com');
  const policies = resources.filter(item => item.Type === 'AWS::IAM::Policy');
  assert.equal(policies.length, 2);
  const actions = policy => policy.Properties.PolicyDocument.Statement.flatMap(item => typeof item.Action === 'string' ? [item.Action] : item.Action).sort();
  const caller = policies.find(item => item.Properties.Roles.includes('github-actions-nonprod-deploy'));
  assert.ok(caller); assert.deepEqual(actions(caller), ['lambda:GetFunctionConfiguration', 'lambda:InvokeFunction']);
  assert.deepEqual(actions(policies.find(item => item !== caller)), ['logs:CreateLogStream', 'logs:PutLogEvents']);
  for (const policy of policies) for (const statement of policy.Properties.PolicyDocument.Statement) {
    assert.notEqual(statement.Resource, '*');
    assert.ok(JSON.stringify(statement.Resource).includes('Fn::GetAtt'), 'Each grant must reference this stack function/log group.');
  }
  const staged = readdirSync(output).filter(name => name.startsWith('asset.') && name.endsWith('.zip'));
  assert.equal(staged.length, 1);
  assert.deepEqual(readFileSync(join(output, staged[0])), readFileSync(bundle), 'CDK must upload exact release ZIP bytes.');
  const failures = [{ environment: 'production' }, { region: 'eu-north-1' }, { accountId: 'bad' }, { acceptanceOrigin: 'https://different.example' },
    { acceptanceOrigin: 'https://fixtureapi123.execute-api.ap-southeast-1.amazonaws.com/path' }, { deploymentRoleArn: 'arn:aws:iam::999988887777:role/github-actions-nonprod-deploy' },
    { offline: 'false' }, { acceptanceBundle: join(directory, 'missing.zip') }];
  for (let index = 0; index < failures.length; index++) assert.notEqual(synth(failures[index], join(directory, `invalid-${index}`)).status, 0, `Invalid context ${index} must reject.`);
  console.log('Regional stack exact code/version, scoped IAM, limits and production/region guards passed.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir())); assert.ok(basename(directory).startsWith('regional-infra-'));
  rmSync(directory, { recursive: true, force: true });
}
