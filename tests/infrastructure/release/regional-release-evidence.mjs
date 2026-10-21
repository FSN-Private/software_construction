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
const verify = () => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', manifestPath, revision, '--require-worker', repository, '67890'], { stdio: 'pipe' });
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
    writeFileSync(join(directory, client.name), 'tampered');
    assert.throws(verify, 'Actual regional bundle corruption must fail the existing verifier.');
    writeFileSync(join(directory, client.name), client.name);
    provenance.subject.find(item => item.name === client.name).digest.sha256 = '0'.repeat(64);
    writeFileSync(join(directory, 'provenance.json'), JSON.stringify(provenance));
    manifest.evidence.find(item => item.kind === 'provenance').digest = `sha256:${digest(readFileSync(join(directory, 'provenance.json')))}`;
    writeFileSync(manifestPath, JSON.stringify(manifest));
    assert.throws(verify, 'Even rechecksummed foreign client provenance must fail.');
  }
  writeFileSync(join(directory, 'unexpected.zip'), 'unapproved');
  assert.throws(generate, 'The new artifact must not make arbitrary artifact sets valid.');
  console.log('Regional bundle release checksum, SBOM/provenance and artifact-set checks passed.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir()));
  assert.ok(basename(directory).startsWith('regional-release-'));
  rmSync(directory, { recursive: true, force: true });
}
