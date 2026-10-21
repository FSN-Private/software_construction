import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { dirname, resolve } from 'node:path';
import { verifyReleaseManifest } from '../packages/release-tools/release-manifest.mjs';
import { bindings, functionArn, validateResult } from './regional-acceptance/contract.mjs';

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
const regional = manifest.artifacts.find(item => item.name === 'regional-acceptance-client.zip');
if (regional) {
  const proof = deployment.regionalAcceptance;
  const arn = functionArn.exec(proof?.functionArn ?? '');
  if (!proof || !arn || arn[1] !== deployment.apiImage.split('.')[0]) throw new Error('Non-production evidence lacks the same-account regional client proof.');
  validateResult({ ...bindings(proof), passed: proof.passed, region: proof.region, functionArn: proof.functionArn, runtime: proof.runtime,
    rendering: deployment.renderingSmoke, ingest: deployment.smoke },
  { schemaVersion: 1, invocationId: proof.invocationId, sourceRevision: expectedSourceRevision, releaseRunId: expectedReleaseRunId,
    manifestSHA256: manifestDigest.slice(7), bundleSHA256: regional.digest.slice(7), origin: proof.origin }, proof.functionArn);
}
verifyReleaseManifest(manifest, dirname(resolve(manifestPath)));
console.log(`Verified non-production promotion evidence for ${expectedSourceRevision}.`);
