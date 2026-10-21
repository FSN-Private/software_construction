import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const directory = mkdtempSync(join(tmpdir(), 'regional-release-'));
const revision = 'a'.repeat(40), repository = 'FSN-Private/software_construction';
const manifestPath = join(directory, 'release-manifest.json');
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const generate = () => execFileSync(process.execPath, ['scripts/generate-release-evidence.mjs', revision, directory, '12345', '67890', repository], { stdio: 'pipe' });
const verify = (mode = '--require-regional') => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', manifestPath, revision, mode, repository, '67890'], { stdio: 'pipe' });
try {
  const base = ['api.tar', 'ingest-worker.tar', 'render-worker.tar', 'web.tar', 'infrastructure.tar', 'platform-api-win-x64.zip'];
  for (const name of [...base, 'regional-acceptance-client.zip']) writeFileSync(join(directory, name), name);
  for (const recovery of [false, true]) {
    if (recovery) writeFileSync(join(directory, 'recovery-worker.tar'), 'recovery');
    generate(); verify();
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
    const provenance = JSON.parse(readFileSync(join(directory, 'provenance.json'), 'utf8'));
    const client = manifest.artifacts.find(item => item.name === 'regional-acceptance-client.zip');
    assert.equal(manifest.artifacts.length, recovery ? 8 : 7);
    assert.equal(client.digest, `sha256:${digest(Buffer.from(client.name))}`);
    assert.equal(provenance.subject.find(item => item.name === client.name).digest.sha256, client.digest.slice(7));
    assert.ok(JSON.parse(readFileSync(join(directory, 'sbom.spdx.json'), 'utf8')).files.some(item => item.fileName === client.name));
    const deploymentPath = join(directory, 'nonprod-deployment.json');
    const id = '00000000-0000-0000-0000-000000000001';
    const image = name => `111122223333.dkr.ecr.ap-southeast-1.amazonaws.com/${name}@sha256:${'e'.repeat(64)}`;
    const deployment = { sourceRevision: revision, releaseRunId: '67890', manifestDigest: `sha256:${digest(readFileSync(manifestPath))}`,
      apiImage: image('api'), workerImage: image('worker'), rendererImage: image('renderer'), taskDefinition: 'api:1', workerTaskDefinition: 'worker:1', rendererTaskDefinition: 'renderer:1', migrationTask: 'migration',
      renderingSmoke: { transport: 'http2', cacheConcurrency: 20, fixtureCount: 5, uncachedRequestStartP95Ms: 100, cacheHitP95Ms: 50, uncachedStartSamples: Array(5).fill(100), cacheSamples: Array(20).fill(50) },
      smoke: { batchId: id, assetId: id, manifestId: id, sha256: 'd'.repeat(64) },
      regionalAcceptance: { schemaVersion: 1, passed: true, invocationId: id, sourceRevision: revision, releaseRunId: '67890', manifestSHA256: digest(readFileSync(manifestPath)), bundleSHA256: client.digest.slice(7),
        origin: 'https://fixtureapi123.execute-api.ap-southeast-1.amazonaws.com', region: 'ap-southeast-1', functionArn: 'arn:aws:lambda:ap-southeast-1:111122223333:function:platform-nonprod-acceptance:7', runtime: 'v24.20.0' } };
    const promote = value => {
      writeFileSync(deploymentPath, JSON.stringify(value));
      return execFileSync(process.execPath, ['scripts/verify-promotion-evidence.mjs', manifestPath, deploymentPath, revision, '67890'], { stdio: 'pipe' });
    };
    promote(deployment);
    for (const change of [value => delete value.regionalAcceptance, value => { value.regionalAcceptance.passed = false; },
      value => { value.regionalAcceptance.bundleSHA256 = '0'.repeat(64); }, value => { value.regionalAcceptance.region = 'eu-north-1'; },
      value => { value.renderingSmoke.cacheSamples.pop(); }, value => { value.regionalAcceptance.functionArn = value.regionalAcceptance.functionArn.replace('111122223333', '999988887777'); }]) {
      const changed = structuredClone(deployment); change(changed); assert.throws(() => promote(changed), 'Regional promotion requires source-bound complete acceptance proof.');
    }
    writeFileSync(join(directory, client.name), 'tampered');
    assert.throws(verify, 'Actual regional bundle corruption must fail the existing verifier.');
    writeFileSync(join(directory, client.name), client.name);
    provenance.subject.find(item => item.name === client.name).digest.sha256 = '0'.repeat(64);
    writeFileSync(join(directory, 'provenance.json'), JSON.stringify(provenance));
    manifest.evidence.find(item => item.kind === 'provenance').digest = `sha256:${digest(readFileSync(join(directory, 'provenance.json')))}`;
    writeFileSync(manifestPath, JSON.stringify(manifest));
    assert.throws(verify, 'Even rechecksummed foreign client provenance must fail.');
  }
  rmSync(join(directory, 'regional-acceptance-client.zip'));
  generate(); verify('--require-worker');
  assert.throws(() => verify(), 'New non-production acceptance must reject historical bundles without the regional client.');
  writeFileSync(join(directory, 'unexpected.zip'), 'unapproved');
  assert.throws(generate, 'The new artifact must not make arbitrary artifact sets valid.');
  console.log('Regional bundle release checksum, SBOM/provenance and artifact-set checks passed.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('regional-release-'));
  rmSync(directory, { recursive: true, force: true });
}
