import { strict as assert } from 'node:assert';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const directory = mkdtempSync(join(tmpdir(), 'recovery-qualification-cli-'));
const revision = '0123456789abcdef0123456789abcdef01234567';
try {
  execFileSync('dotnet', ['tests/recovery/bin/Debug/net10.0/Platform.Recovery.Tests.dll',
    '--write-qualification-fixture', directory], { env: { ...process.env, RECOVERY_FIXTURE_SOURCE_REVISION: revision }, stdio: 'pipe' });
  const paths = ['recovery-qualification.v1.json', 'recovery-trust.json', 'release-binding.json',
    'recovery-audit-checkpoint.v1.json', 'recovery-offline-anchor.json'].map((name) => join(directory, name));
  const verify = () => spawnSync('dotnet', ['artifacts/recovery-worker/Platform.Recovery.Worker.dll',
    'verify-qualification', ...paths], { encoding: 'utf8' });
  const valid = verify();
  assert.equal(valid.status, 0, valid.stderr);
  assert.match(valid.stdout, /recovery_qualification_verified/);
  const past = new Date();
  past.setUTCMonth(past.getUTCMonth() - 4);
  for (const [name, time] of [['expired', past], ['future', new Date(Date.now() + 10 * 60 * 1000)]]) {
    const staleDirectory = join(directory, name);
    execFileSync('dotnet', ['tests/recovery/bin/Debug/net10.0/Platform.Recovery.Tests.dll',
      '--write-qualification-fixture', staleDirectory], { env: { ...process.env,
      RECOVERY_FIXTURE_SOURCE_REVISION: revision, RECOVERY_FIXTURE_NOW_UTC: time.toISOString() }, stdio: 'pipe' });
    const stalePaths = paths.map((path) => join(staleDirectory, path.split(/[\\/]/).at(-1)));
    const rejected = spawnSync('dotnet', ['artifacts/recovery-worker/Platform.Recovery.Worker.dll',
      'verify-qualification', ...stalePaths], { encoding: 'utf8' });
    assert.equal(rejected.status, 1, rejected.stdout);
    assert.match(rejected.stderr, /qualification_expired_or_future/);
  }
  for (const [index, mutate] of [
    [2, (value) => { value.apiBuildDigest = 'f'.repeat(64); }],
    [2, (value) => { value.configurationDigest = 'f'.repeat(64); }],
    [2, (value) => { value.ownerAccessEvidenceSha256 = 'f'.repeat(64); }],
    [4, (value) => { value.headCheckpointSha256 = 'f'.repeat(64); }],
    [1, (value) => { value.publicKeySha256 = 'f'.repeat(64); }],
    [0, (value) => { value.payload.restore.rpoSeconds = 1; }],
    [3, (value) => { value.checkpoint.signatureBase64 = Buffer.alloc(256).toString('base64'); }],
  ]) {
    const original = readFileSync(paths[index]);
    const changed = JSON.parse(original);
    mutate(changed);
    writeFileSync(paths[index], JSON.stringify(changed));
    const rejected = verify();
    assert.equal(rejected.status, 1, rejected.stdout);
    assert.match(rejected.stderr, /recovery_qualification_rejected/);
    writeFileSync(paths[index], original);
  }
  const environment = { ...process.env, RECOVERY_QUALIFICATION_PATH: paths[0], RECOVERY_TRUST_PATH: paths[1],
    RECOVERY_RELEASE_BINDING_PATH: paths[2], RECOVERY_CHECKPOINT_PATH: paths[3], RECOVERY_OFFLINE_ANCHOR_PATH: paths[4] };
  for (const artifact of ['api.tar', 'ingest-worker.tar', 'render-worker.tar', 'recovery-worker.tar',
    'web.tar', 'infrastructure.tar', 'platform-api-win-x64.zip']) writeFileSync(join(directory, artifact), artifact);
  execFileSync(process.execPath, ['scripts/generate-release-evidence.mjs', revision, directory, '123', '456', 'owner/repo'], { env: environment, stdio: 'pipe' });
  execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'),
    revision, '--require-recovery', 'owner/repo', '456'], { env: environment, stdio: 'pipe' });
  const attached = join(directory, 'recovery-checkpoint.json');
  writeFileSync(attached, `${readFileSync(attached, 'utf8')} `);
  assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'),
    revision, '--require-recovery', 'owner/repo', '456'], { env: environment, stdio: 'pipe' }), /Digest verification failed/);
  console.log('Offline qualification CLI and Node release attachment pass signed fixture and reject tampered bindings, trust, checkpoint and evidence. Test fixtures establish no cloud qualification.');
} finally {
  assert.ok(resolve(directory).startsWith(resolve(join(tmpdir(), 'recovery-qualification-cli-'))));
  rmSync(directory, { recursive: true, force: true });
}
