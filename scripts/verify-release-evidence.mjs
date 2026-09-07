import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { verifyReleaseManifest } from '../packages/release-tools/release-manifest.mjs';

const [manifestPath, expectedSourceRevision] = process.argv.slice(2);
if (!manifestPath || !/^[a-f0-9]{40}$/.test(expectedSourceRevision)) {
  throw new Error('Usage: verify-release-evidence.mjs <manifest-path> <full-source-sha>');
}

const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
if (manifest.sourceRevision !== expectedSourceRevision) {
  throw new Error('Release manifest source revision does not match the requested revision.');
}
if (manifest.artifacts.length !== 4) {
  throw new Error('Release manifest must contain exactly the four platform artifacts.');
}
verifyReleaseManifest(manifest, dirname(resolve(manifestPath)));
console.log(`Verified immutable release evidence for ${expectedSourceRevision}.`);
