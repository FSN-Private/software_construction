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
execFileSync(process.execPath, ['scripts/generate-release-evidence.mjs', revision, directory, '12345', '67890', 'FSN-Private/software_construction'], { stdio: 'inherit' });
execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), revision, '--require-worker', '67890'], { stdio: 'inherit' });

const manifest = JSON.parse(readFileSync(join(directory, 'release-manifest.json'), 'utf8'));
const sbom = JSON.parse(readFileSync(join(directory, 'sbom.spdx.json'), 'utf8'));
const provenanceText = readFileSync(join(directory, 'provenance.json'), 'utf8');
const provenance = JSON.parse(provenanceText);
assert.equal(manifest.sourceRevision, revision);
assert.equal(manifest.artifacts.length, 5);
assert.equal(manifest.evidence.length, 3);
assert.equal(sbom.files.length, 5);
assert.equal(provenance.predicate.buildDefinition.externalParameters.sourceRevision, revision);
assert.equal(provenance.predicate.buildDefinition.externalParameters.ciRunId, '12345');
assert.equal(provenance.predicate.runDetails.metadata.invocationId, 'https://github.com/FSN-Private/software_construction/actions/runs/67890');
assert.deepEqual(provenance.subject.map(({ name }) => name).sort(), manifest.artifacts.map(({ name }) => name).sort());
assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), revision, '--require-worker', '67891'], { stdio: 'pipe' }), /Provenance release workflow run does not match/);
const legacy = structuredClone(manifest);
legacy.artifacts = legacy.artifacts.filter(({ name }) => name !== 'ingest-worker.tar');
writeFileSync(join(directory, 'provenance.json'), JSON.stringify({ predicateType: 'https://slsa.dev/provenance/v1', buildDefinition: { externalParameters: { sourceRevision: revision } } }));
legacy.evidence.find(({ kind }) => kind === 'provenance').digest = `sha256:${createHash('sha256').update(readFileSync(join(directory, 'provenance.json'))).digest('hex')}`;
writeFileSync(join(directory, 'legacy-release-manifest.json'), JSON.stringify(legacy));
execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'legacy-release-manifest.json'), revision], { stdio: 'inherit' });
assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'legacy-release-manifest.json'), revision, '--require-worker'], { stdio: 'pipe' }));
const manifestDigest = `sha256:${createHash('sha256').update(readFileSync(join(directory, 'release-manifest.json'))).digest('hex')}`;
writeFileSync(join(directory, 'provenance.json'), provenanceText);
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
provenance.predicate.buildDefinition.externalParameters.sourceRevision = 'fedcba9876543210fedcba9876543210fedcba98';
writeFileSync(join(directory, 'provenance.json'), JSON.stringify(provenance));
manifest.evidence.find(({ kind }) => kind === 'provenance').digest = `sha256:${createHash('sha256').update(readFileSync(join(directory, 'provenance.json'))).digest('hex')}`;
writeFileSync(join(directory, 'release-manifest.json'), JSON.stringify(manifest));
assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), revision], { stdio: 'pipe' }), /Provenance source or CI run binding is invalid/);
console.log('Release evidence generation tests passed.');
