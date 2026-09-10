import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { verifyReleaseManifest } from '../packages/release-tools/release-manifest.mjs';

const [manifestPath, expectedSourceRevision, mode] = process.argv.slice(2);
if (!manifestPath || !/^[a-f0-9]{40}$/.test(expectedSourceRevision)) {
  throw new Error('Usage: verify-release-evidence.mjs <manifest-path> <full-source-sha>');
}

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
if (manifest.sourceRevision !== expectedSourceRevision) {
  throw new Error('Release manifest source revision does not match the requested revision.');
}
const names = manifest.artifacts.map(({ name }) => name).sort();
const legacy = ['api.tar', 'infrastructure.tar', 'platform-api-win-x64.zip', 'web.tar'];
const current = [...legacy, 'ingest-worker.tar'].sort();
if (JSON.stringify(names) !== JSON.stringify(current) && JSON.stringify(names) !== JSON.stringify(legacy)) throw new Error('Release manifest contains an unsupported artifact set.');
if (mode === '--require-worker' && !names.includes('ingest-worker.tar')) throw new Error('Release manifest does not contain the required ingest worker artifact.');
verifyReleaseManifest(manifest, dirname(resolve(manifestPath)));
console.log(`Verified immutable release evidence for ${expectedSourceRevision}.`);
