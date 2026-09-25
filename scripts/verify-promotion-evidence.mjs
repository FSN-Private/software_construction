import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { verifyReleaseManifest } from '../packages/release-tools/release-manifest.mjs';

const [manifestPath, deploymentPath, expectedSourceRevision, expectedReleaseRunId] = process.argv.slice(2);
if (!manifestPath || !deploymentPath || !/^[a-f0-9]{40}$/.test(expectedSourceRevision) || !/^\d+$/.test(expectedReleaseRunId)) {
  throw new Error('Usage: verify-promotion-evidence.mjs <manifest> <nonprod-deployment> <full-source-sha> <release-run-id>');
}
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const deployment = JSON.parse(readFileSync(deploymentPath, 'utf8'));
const manifestDigest = `sha256:${createHash('sha256').update(readFileSync(manifestPath)).digest('hex')}`;
if (manifest.sourceRevision !== expectedSourceRevision || deployment.sourceRevision !== expectedSourceRevision) {
  throw new Error('Promotion evidence source revision does not match the versioned release.');
}
if (String(deployment.releaseRunId) !== expectedReleaseRunId || deployment.manifestDigest !== manifestDigest) {
  throw new Error('Promotion evidence does not reference this exact verified release manifest.');
}
if (!/^.+@sha256:[a-f0-9]{64}$/.test(deployment.apiImage ?? '')) {
  throw new Error('Non-production evidence does not contain an immutable API registry digest.');
}
if (!/^.+@sha256:[a-f0-9]{64}$/.test(deployment.workerImage ?? '')) {
  throw new Error('Non-production evidence does not contain an immutable worker registry digest.');
}
if (!/^.+@sha256:[a-f0-9]{64}$/.test(deployment.rendererImage ?? '')) {
  throw new Error('Non-production evidence does not contain an immutable renderer registry digest.');
}
if (!deployment.taskDefinition || !deployment.workerTaskDefinition || !deployment.rendererTaskDefinition || !deployment.migrationTask || !deployment.smoke?.assetId) {
  throw new Error('Non-production evidence does not prove the coordinated migration, deployment, and smoke test.');
}
verifyReleaseManifest(manifest, dirname(resolve(manifestPath)));
console.log(`Verified non-production promotion evidence for ${expectedSourceRevision}.`);
