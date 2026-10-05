import { createHash } from 'node:crypto';
import { readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { recoveryVerificationConfiguration, verifyRecoveryQualification } from './recovery-release-evidence.mjs';

function imageDigest(image, account, region) {
  const match = /^(\d{12})\.dkr\.ecr\.([a-z0-9-]+)\.amazonaws\.com\/[a-z0-9._/-]+@sha256:([a-f0-9]{64})$/.exec(image ?? '');
  if (!match || match[1] !== account || match[2] !== region) throw new Error('Recovery promotion requires a resolved immutable image in the expected account and region.');
  return match[3];
}

export function migrationDigest(directory = 'apps/api/Database/Migrations') {
  const files = readdirSync(directory).filter((name) => name.endsWith('.sql')).sort();
  if (!files.length) throw new Error('Recovery promotion requires migration artifacts.');
  const digest = createHash('sha256');
  for (const name of files) digest.update(`${name}\0`).update(readFileSync(resolve(directory, name)));
  return digest.digest('hex');
}

export function promotionBinding(environment = process.env) {
  if (!/^[a-f0-9]{40}$/.test(environment.SOURCE_REVISION ?? '') ||
      !/^[a-f0-9]{64}$/.test(environment.RECOVERY_TEMPLATE_DIGEST ?? '') ||
      !/^[a-f0-9]{64}$/.test(environment.RECOVERY_CONFIGURATION_DIGEST ?? '') ||
      !/^[a-f0-9]{64}$/.test(environment.RECOVERY_OWNER_ACCESS_EVIDENCE_DIGEST ?? '') ||
      !/^\d{12}$/.test(environment.PRIMARY_ACCOUNT_ID ?? '') ||
      !/^\d{12}$/.test(environment.RECOVERY_ACCOUNT_ID ?? '') ||
      environment.PRIMARY_ACCOUNT_ID === environment.RECOVERY_ACCOUNT_ID)
    throw new Error('Recovery promotion requires the exact source revision and independently approved template digest.');
  return {
    sourceRevision: environment.SOURCE_REVISION,
    recoveryTemplateDigest: environment.RECOVERY_TEMPLATE_DIGEST,
    apiBuildDigest: imageDigest(environment.API_IMAGE, environment.PRIMARY_ACCOUNT_ID, 'ap-southeast-1'),
    recoveryWorkerBuildDigest: imageDigest(environment.RECOVERY_WORKER_IMAGE, environment.RECOVERY_ACCOUNT_ID, 'ap-southeast-2'),
    ingestWorkerBuildDigest: imageDigest(environment.WORKER_IMAGE, environment.PRIMARY_ACCOUNT_ID, 'ap-southeast-1'),
    rendererBuildDigest: imageDigest(environment.RENDERER_IMAGE, environment.PRIMARY_ACCOUNT_ID, 'ap-southeast-1'),
    migrationDigest: migrationDigest(),
    configurationDigest: environment.RECOVERY_CONFIGURATION_DIGEST,
    ownerAccessEvidenceSha256: environment.RECOVERY_OWNER_ACCESS_EVIDENCE_DIGEST,
  };
}

export function promotionImages(deployment, environment) {
  if (deployment.sourceRevision !== environment.SOURCE_REVISION ||
      environment.ECR_REGISTRY !== `${environment.PRIMARY_ACCOUNT_ID}.dkr.ecr.ap-southeast-1.amazonaws.com`)
    throw new Error('Recovery promotion image evidence belongs to a different release or registry.');
  const result = { ...environment };
  for (const [setting, repository, field] of [['API_IMAGE', 'API_REPOSITORY', 'apiImage'],
    ['WORKER_IMAGE', 'WORKER_REPOSITORY', 'workerImage'], ['RENDERER_IMAGE', 'RENDERER_REPOSITORY', 'rendererImage']]) {
    const match = /^\d{12}\.dkr\.ecr\.ap-southeast-1\.amazonaws\.com\/[a-z0-9._/-]+@sha256:([a-f0-9]{64})$/.exec(deployment[field] ?? '');
    if (!match || !/^[a-z0-9._/-]+$/.test(environment[repository] ?? ''))
      throw new Error('Recovery promotion requires immutable non-production deployment image evidence.');
    result[setting] = `${environment.ECR_REGISTRY}/${environment[repository]}@sha256:${match[1]}`;
  }
  return result;
}

export function verifyPromotion(environment = process.env) {
  const binding = promotionBinding(environment);
  const configuration = recoveryVerificationConfiguration(environment);
  const qualification = JSON.parse(readFileSync(configuration.qualification, 'utf8'));
  if (qualification.payload?.primary?.accountId !== environment.PRIMARY_ACCOUNT_ID ||
      qualification.payload?.primary?.region !== 'ap-southeast-1' ||
      qualification.payload?.recovery?.accountId !== environment.RECOVERY_ACCOUNT_ID ||
      qualification.payload?.recovery?.region !== 'ap-southeast-2')
    throw new Error('Recovery qualification belongs to a different deployment environment.');
  // Compute expected image and migration bindings independently of the signed report.
  // Retain these safe values with the deployment record for subsequent verification.
  const bindingPath = resolve(environment.RELEASE_DIR ?? 'artifacts/release', 'recovery-promotion-binding.json');
  writeFileSync(bindingPath, JSON.stringify(binding), { mode: 0o600 });
  verifyRecoveryQualification({ ...configuration, binding: bindingPath, sourceRevision: binding.sourceRevision });
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const arguments_ = process.argv.slice(2);
  if (!arguments_.length) verifyPromotion();
  else if (arguments_.length === 2 && arguments_[0] === '--from-nonprod')
    verifyPromotion(promotionImages(JSON.parse(readFileSync(arguments_[1], 'utf8')), process.env));
  else throw new Error('Usage: verify-recovery-promotion.mjs [--from-nonprod <verified-deployment-file>]');
}
