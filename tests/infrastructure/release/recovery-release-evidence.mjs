import { strict as assert } from 'node:assert';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { verifyRecoveryQualification } from '../../../scripts/recovery-release-evidence.mjs';
import { migrationDigest, promotionBinding, promotionImages } from '../../../scripts/verify-recovery-promotion.mjs';

const directory = mkdtempSync(join(tmpdir(), 'recovery-release-'));
const revision = '0123456789abcdef0123456789abcdef01234567';
try {
  for (const artifact of ['api.tar', 'ingest-worker.tar', 'render-worker.tar', 'recovery-worker.tar',
    'web.tar', 'infrastructure.tar', 'platform-api-win-x64.zip']) writeFileSync(join(directory, artifact), artifact);
  const environment = { ...process.env };
  for (const key of ['RECOVERY_QUALIFICATION_PATH', 'RECOVERY_CHECKPOINT_PATH', 'RECOVERY_RELEASE_BINDING_PATH',
    'RECOVERY_TRUST_PATH', 'RECOVERY_OFFLINE_ANCHOR_PATH']) delete environment[key];
  execFileSync(process.execPath, ['scripts/generate-release-evidence.mjs', revision, directory, '123', '456', 'owner/repo'], { env: environment });
  const path = join(directory, 'release-manifest.json');
  const manifest = JSON.parse(readFileSync(path, 'utf8'));
  assert.equal(manifest.artifacts.length, 7);
  assert.equal(manifest.compatibility.recoveryRequired, true);
  // A packaged candidate is not successful cloud qualification.
  execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', path, revision, '--require-worker', 'owner/repo', '456'], { env: environment });
  assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', path, revision,
    '--require-recovery', 'owner/repo', '456'], { env: environment, stdio: 'pipe' }), /missing its required recovery evidence/);
  manifest.artifacts = manifest.artifacts.filter(({ name }) => name !== 'recovery-worker.tar');
  writeFileSync(path, JSON.stringify(manifest));
  assert.throws(() => execFileSync(process.execPath, ['scripts/verify-release-evidence.mjs', path, revision,
    '--require-recovery', 'owner/repo', '456'], { env: environment, stdio: 'pipe' }), /required recovery worker artifact/);
  assert.throws(() => verifyRecoveryQualification({ sourceRevision: revision }), /missing or exceeds/);
  const image = (account, region, digest) => `${account}.dkr.ecr.${region}.amazonaws.com/platform@sha256:${digest.repeat(64)}`;
  const deployment = { SOURCE_REVISION: revision, PRIMARY_ACCOUNT_ID: '111122223333', RECOVERY_ACCOUNT_ID: '444455556666',
    RECOVERY_TEMPLATE_DIGEST: 'a'.repeat(64), RECOVERY_CONFIGURATION_DIGEST: 'f'.repeat(64),
    RECOVERY_OWNER_ACCESS_EVIDENCE_DIGEST: '1'.repeat(64), API_IMAGE: image('111122223333', 'ap-southeast-1', 'b'),
    WORKER_IMAGE: image('111122223333', 'ap-southeast-1', 'c'), RENDERER_IMAGE: image('111122223333', 'ap-southeast-1', 'd'),
    RECOVERY_WORKER_IMAGE: image('444455556666', 'ap-southeast-2', 'e') };
  const binding = promotionBinding(deployment);
  assert.equal(binding.apiBuildDigest, 'b'.repeat(64));
  assert.equal(binding.migrationDigest, migrationDigest());
  const registry = '111122223333.dkr.ecr.ap-southeast-1.amazonaws.com';
  const beforeCredentials = { ...deployment, ECR_REGISTRY: registry, API_REPOSITORY: 'api', WORKER_REPOSITORY: 'ingest', RENDERER_REPOSITORY: 'render' };
  const nonprod = { sourceRevision: revision, apiImage: deployment.API_IMAGE, workerImage: deployment.WORKER_IMAGE, rendererImage: deployment.RENDERER_IMAGE };
  const images = promotionImages(nonprod, beforeCredentials);
  assert.equal(images.API_IMAGE, `${registry}/api@sha256:${'b'.repeat(64)}`);
  assert.throws(() => promotionImages({ ...nonprod, sourceRevision: 'f'.repeat(40) }, beforeCredentials));
  assert.throws(() => promotionImages(nonprod, { ...beforeCredentials, ECR_REGISTRY: 'foreign-registry' }));
  for (const change of [{ PRIMARY_ACCOUNT_ID: '444455556666' }, { RECOVERY_TEMPLATE_DIGEST: '' },
    { API_IMAGE: deployment.API_IMAGE.replace('@sha256:', ':') },
    { RECOVERY_WORKER_IMAGE: image('111122223333', 'ap-southeast-2', 'e') },
    { API_IMAGE: image('111122223333', 'ap-southeast-2', 'b') }])
    assert.throws(() => promotionBinding({ ...deployment, ...change }));
  const script = readFileSync('scripts/deploy-ecs-release.sh', 'utf8');
  assert.ok(script.indexOf('verify-recovery-promotion.mjs') < script.indexOf('aws ecs register-task-definition'));
  assert.ok(script.indexOf('verify-recovery-promotion.mjs') < script.indexOf('scripts/run-ecs-migration.sh'));
  assert.ok(script.includes('elif test -n "${RELEASE_TAG:-}"'));
  const production = readFileSync('.github/workflows/release.yml', 'utf8');
  assert.ok(production.includes("RECOVERY_REQUIRED: 'true'"));
  assert.ok(production.includes('secrets.AWS_RECOVERY_SIGNING_TRUST'));
  assert.ok(production.includes('secrets.AWS_RECOVERY_OFFLINE_AUDIT_ANCHOR'));
  assert.ok(production.indexOf('verify-recovery-promotion.mjs --from-nonprod') < production.indexOf('uses: aws-actions/configure-aws-credentials@'));
  console.log('Recovery release candidates require independently verified qualification before promotion.');
} finally {
  assert.ok(resolve(directory).startsWith(resolve(join(tmpdir(), 'recovery-release-'))));
  rmSync(directory, { recursive: true, force: true });
}
