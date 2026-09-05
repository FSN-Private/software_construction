import { strict as assert } from 'node:assert';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const directory = mkdtempSync(join(tmpdir(), 'release-evidence-'));
for (const artifact of ['api.tar', 'web.tar', 'infrastructure.tar', 'platform-api-win-x64.zip']) {
  writeFileSync(join(directory, artifact), artifact);
}
const revision = '0123456789abcdef0123456789abcdef01234567';
execFileSync(process.execPath, ['scripts/generate-release-evidence.mjs', revision, directory], { stdio: 'inherit' });
execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), revision], { stdio: 'inherit' });

const manifest = JSON.parse(readFileSync(join(directory, 'release-manifest.json'), 'utf8'));
const sbom = JSON.parse(readFileSync(join(directory, 'sbom.spdx.json'), 'utf8'));
assert.equal(manifest.sourceRevision, revision);
assert.equal(manifest.artifacts.length, 4);
assert.equal(manifest.evidence.length, 3);
assert.equal(sbom.files.length, 4);
assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', join(directory, 'release-manifest.json'), 'fedcba9876543210fedcba9876543210fedcba98'], { stdio: 'pipe' }));
console.log('Release evidence generation tests passed.');
