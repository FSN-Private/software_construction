import { strict as assert } from 'node:assert';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const output = mkdtempSync(join(tmpdir(), 'platform-context-contract-'));
const context = {
  environment: 'nonprod', region: 'ap-southeast-1', monthlyBudgetUsd: '20',
  ownerEmail: 'owner@example.invalid', alertEmail: 'alerts@example.invalid',
  webOrigin: 'https://example.invalid', cognitoDomainPrefix: 'platform-context-contract',
  deploymentRoleArn: 'arn:aws:iam::111122223333:role/github-actions-nonprod-deploy',
};
function synth(extra = {}) {
  return spawnSync(process.execPath, ['packages/infrastructure/dist/app.js'], {
    encoding: 'utf8', env: { ...process.env, CDK_OUTDIR: output,
      CDK_CONTEXT_JSON: JSON.stringify({ ...context, ...extra }) },
  });
}
function rejects(extra, message) {
  const result = synth(extra);
  assert.notEqual(result.status, 0, `Expected rejection: ${message}`);
  assert.match(result.stderr, message);
}
try {
  for (const recovery of [undefined, 'false']) {
    const result = synth({ recovery });
    assert.equal(result.status, 0, result.stderr);
    const template = JSON.parse(readFileSync(join(output, 'PlatformFoundation-nonprod.template.json'), 'utf8'));
    assert.ok(Object.values(template.Resources).some(resource => resource.Type === 'AWS::CloudWatch::Alarm' &&
      resource.Properties.MetricName === 'BackupAgeHours'), 'Keep the existing alarm until recovery monitoring replaces it.');
    assert.ok(!JSON.stringify(template).includes('recovery-capture'));
  }
  const enabled = { recovery: 'true', primaryAccountId: '111122223333', recoveryAccountId: '444455556666' };
  rejects(enabled, /context recoveryMigrationDigest is required/);
  rejects({ ...enabled, recoveryMigrationDigest: '0'.repeat(64) }, /must match the ordered SQL/);
  const migrations = resolve('apps/api/Database/Migrations');
  const digest = createHash('sha256');
  for (const name of readdirSync(migrations).filter(name => name.endsWith('.sql')).sort())
    digest.update(name).update('\0').update(readFileSync(join(migrations, name)));
  enabled.recoveryMigrationDigest = digest.digest('hex');
  rejects(enabled, /context recoveryDatabaseRootCertificate is required/);
  enabled.recoveryDatabaseRootCertificate = 'test certificate bytes';
  rejects(enabled, /context recoveryDatabaseRootCertificateSha256 is required/);
  rejects({ ...enabled, recoveryDatabaseRootCertificateSha256: '0'.repeat(64) }, /must match the exact database root certificate/);
  enabled.recoveryDatabaseRootCertificateSha256 = createHash('sha256').update(enabled.recoveryDatabaseRootCertificate).digest('hex');
  for (const name of ['recoveryApiImageDigest', 'recoveryIngestWorkerImageDigest', 'recoveryRendererImageDigest',
    'recoveryTemplateDigest', 'recoveryConfigurationDigest']) {
    rejects(enabled, new RegExp(`context ${name} is required`));
    enabled[name] = 'a'.repeat(64);
  }
  rejects({ ...enabled, recoveryApiImageDigest: 'invalid' }, /context recoveryApiImageDigest must be a SHA-256 digest/);
  console.log('Primary-only contexts and recovery-enabled fail-closed deployment validation passed.');
} finally {
  assert.equal(resolve(output).startsWith(resolve(tmpdir()) + '\\') || resolve(output).startsWith(resolve(tmpdir()) + '/'), true);
  rmSync(output, { recursive: true, force: true });
}
