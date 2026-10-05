import { execFileSync } from 'node:child_process';
import { copyFileSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { sha256File } from '../packages/release-tools/release-manifest.mjs';

const evidenceKinds = ['recovery-qualification', 'recovery-checkpoint', 'recovery-binding'];
const evidenceNames = ['recovery-qualification.json', 'recovery-checkpoint.json', 'recovery-release-binding.json'];
const digestFields = ['recoveryTemplateDigest', 'apiBuildDigest', 'recoveryWorkerBuildDigest',
  'ingestWorkerBuildDigest', 'rendererBuildDigest', 'migrationDigest', 'configurationDigest', 'ownerAccessEvidenceSha256'];

function requireFile(path, maximumBytes) {
  if (!path || !statSync(path).isFile() || statSync(path).size === 0 || statSync(path).size > maximumBytes)
    throw new Error('Recovery verification input is missing or exceeds its size limit.');
}

export function verifyRecoveryQualification({ qualification, trust, binding, checkpoint, anchor, sourceRevision }) {
  requireFile(qualification, 1024 * 1024);
  requireFile(trust, 16 * 1024);
  requireFile(binding, 16 * 1024);
  requireFile(checkpoint, 64 * 1024 * 1024);
  requireFile(anchor, 1024 * 1024);
  const release = JSON.parse(readFileSync(binding, 'utf8'));
  if (release.sourceRevision !== sourceRevision || digestFields.some((field) => !/^[a-f0-9]{64}$/.test(release[field])))
    throw new Error('Recovery qualification release binding is invalid.');
  // The shared .NET codec owns canonical bytes, pinned RSA-PSS trust, checkpoint
  // anchoring, calendar freshness, timelines and every qualification criterion.
  execFileSync('dotnet', [resolve('artifacts/recovery-worker/Platform.Recovery.Worker.dll'),
    'verify-qualification', qualification, trust, binding, checkpoint, anchor], { stdio: 'inherit' });
}

export function attachRecoveryEvidence(manifest, outputDirectory, configuration) {
  verifyRecoveryQualification({ ...configuration, sourceRevision: manifest.sourceRevision });
  const paths = [configuration.qualification, configuration.checkpoint, configuration.binding];
  for (let index = 0; index < paths.length; index++) {
    const target = join(outputDirectory, evidenceNames[index]);
    if (resolve(paths[index]) !== resolve(target)) copyFileSync(paths[index], target);
    manifest.evidence.push({ kind: evidenceKinds[index], name: evidenceNames[index], digest: sha256File(target) });
  }
  // Trust and the offline anchor are operator-provisioned inputs, never selected
  // from the release artifact being verified.
  manifest.compatibility.recoveryRequired = true;
}

export function verifyRecoveryEvidence(manifest, baseDirectory, configuration) {
  const paths = evidenceKinds.map((kind, index) => {
    const entries = manifest.evidence.filter((entry) => entry.kind === kind);
    if (entries.length !== 1 || entries[0].name !== evidenceNames[index])
      throw new Error('Release is missing its required recovery evidence.');
    const path = join(baseDirectory, entries[0].name);
    if (sha256File(path) !== entries[0].digest) throw new Error('Recovery evidence digest mismatch.');
    return path;
  });
  verifyRecoveryQualification({ qualification: paths[0], checkpoint: paths[1], binding: paths[2],
    trust: configuration.trust, anchor: configuration.anchor, sourceRevision: manifest.sourceRevision });
}

export function recoveryVerificationConfiguration(environment = process.env) {
  return { qualification: environment.RECOVERY_QUALIFICATION_PATH, checkpoint: environment.RECOVERY_CHECKPOINT_PATH,
    binding: environment.RECOVERY_RELEASE_BINDING_PATH, trust: environment.RECOVERY_TRUST_PATH,
    anchor: environment.RECOVERY_OFFLINE_ANCHOR_PATH };
}
