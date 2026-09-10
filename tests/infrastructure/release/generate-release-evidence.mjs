import { strict as assert } from 'node:assert';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const directory = mkdtempSync(join(tmpdir(), 'release-evidence-'));
for (const artifact of ['api.tar', 'ingest-worker.tar', 'web.tar', 'infrastructure.tar', 'platform-api-win-x64.zip']) {
  writeFileSync(join(directory, artifact), artifact);
}
const revision = '0123456789abcdef0123456789abcdef01234567';
execFileSync(process.execPath, ['scripts/generate-release-evidence.mjs', revision, directory], { stdio: 'inherit' });
execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), revision], { stdio: 'inherit' });

const manifest = JSON.parse(readFileSync(join(directory, 'release-manifest.json'), 'utf8'));
const sbom = JSON.parse(readFileSync(join(directory, 'sbom.spdx.json'), 'utf8'));
assert.equal(manifest.sourceRevision, revision);
assert.equal(manifest.artifacts.length, 5);
assert.equal(manifest.evidence.length, 3);
assert.equal(sbom.files.length, 5);
const legacy = { ...manifest, artifacts: manifest.artifacts.filter(({ name }) => name !== 'ingest-worker.tar') };
writeFileSync(join(directory, 'legacy-release-manifest.json'), JSON.stringify(legacy));
execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'legacy-release-manifest.json'), revision], { stdio: 'inherit' });
assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'legacy-release-manifest.json'), revision, '--require-worker'], { stdio: 'pipe' }));
const manifestDigest = `sha256:${createHash('sha256').update(readFileSync(join(directory, 'release-manifest.json'))).digest('hex')}`;
writeFileSync(join(directory, 'nonprod-deployment.json'), JSON.stringify({
  sourceRevision: revision,
  releaseRunId: '12345',
  manifestDigest,
  apiImage: 'example.dkr.ecr.ap-southeast-1.amazonaws.com/platform-api@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef',
  workerImage: 'example.dkr.ecr.ap-southeast-1.amazonaws.com/platform-worker@sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789',
  taskDefinition: 'arn:api-task',
  workerTaskDefinition: 'arn:worker-task',
  migrationTask: 'arn:migration-task',
  smoke: { assetId: '00000000-0000-0000-0000-000000000001' },
}));
execFileSync(process.execPath, ['scripts/verify-promotion-evidence.mjs', join(directory, 'release-manifest.json'), join(directory, 'nonprod-deployment.json'), revision, '12345'], { stdio: 'inherit' });
assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), 'fedcba9876543210fedcba9876543210fedcba98'], { stdio: 'pipe' }));
console.log('Release evidence generation tests passed.');
