import { strict as assert } from 'node:assert';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createReleaseManifest, verifyReleaseManifest } from '../../packages/release-tools/release-manifest.mjs';

const directory = mkdtempSync(join(tmpdir(), 'release-manifest-'));
const artifact = join(directory, 'api.tar');
const sbom = join(directory, 'sbom.spdx.json');
const provenance = join(directory, 'provenance.json');
writeFileSync(artifact, 'immutable artifact');
writeFileSync(sbom, '{"spdxVersion":"SPDX-2.3"}');
writeFileSync(provenance, '{"predicateType":"https://slsa.dev/provenance/v1"}');

const manifest = createReleaseManifest({
  releaseId: 'v0.0.1',
  sourceRevision: '0123456789abcdef0123456789abcdef01234567',
  artifacts: [artifact],
  sbom,
  provenance,
  compatibility: { rollbackAllowed: true, schemaVersion: 'v1' },
});

assert.equal(verifyReleaseManifest(manifest, directory), true);
writeFileSync(artifact, 'tampered artifact');
assert.throws(() => verifyReleaseManifest(manifest, directory), /Digest verification failed/);
manifest.compatibility.rollbackAllowed = false;
assert.throws(() => verifyReleaseManifest(manifest, directory), /Rollback is refused/);

console.log('Release manifest tooling tests passed.');
