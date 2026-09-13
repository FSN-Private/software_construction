import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const ci = readFileSync('.github/workflows/ci.yml', 'utf8');
const deploy = readFileSync('.github/workflows/deploy-nonprod.yml', 'utf8');
const lifecycle = readFileSync('.github/workflows/nonprod-lifecycle.yml', 'utf8');
const release = readFileSync('.github/workflows/release.yml', 'utf8');
const rollback = readFileSync('.github/workflows/rollback.yml', 'utf8');
const deployScript = readFileSync('scripts/deploy-ecs-release.sh', 'utf8');
const lifecycleScript = readFileSync('scripts/manage-nonprod-lifecycle.sh', 'utf8');
const rollbackScript = readFileSync('scripts/rollback-ecs-release.sh', 'utf8');

function concurrencyGroup(workflow) {
  return workflow.match(/concurrency:\r?\n\s+group:\s+([^\r\n]+)/)?.[1].trim();
}

function cancelInProgress(workflow) {
  return workflow.match(/cancel-in-progress:\s+(true|false)/)?.[1] === 'true';
}

function timeoutMinutes(workflow) {
  return [...workflow.matchAll(/timeout-minutes:\s+(\d+)/g)].map((match) => Number(match[1]));
}

function scheduleWorkflow(runs, run) {
  const active = runs.filter((candidate) => candidate.group === run.group && candidate.state === 'active');
  for (const candidate of active) {
    if (run.cancelInProgress) {
      candidate.state = 'cancelled';
    } else {
      run.state = 'queued';
    }
  }
  runs.push({ ...run, state: run.state ?? 'active' });
  return runs;
}

function canRollback(manifest, promotionEvidence, requestedTaskDefinition, requestedWorkerTaskDefinition) {
  return (
    manifest.compatibility?.rollbackAllowed === true &&
    promotionEvidence.taskDefinition === requestedTaskDefinition &&
    promotionEvidence.workerTaskDefinition === requestedWorkerTaskDefinition &&
    promotionEvidence.sourceRevision === manifest.sourceRevision &&
    promotionEvidence.releaseArtifactDigest === manifest.apiDigest &&
    promotionEvidence.workerReleaseArtifactDigest === manifest.workerDigest
  );
}

assert.equal(cancelInProgress(ci), true, 'Superseded CI revisions must cancel.');
assert.match(ci, /if: \$\{\{ always\(\) \}\}/, 'Required aggregation must run after cancellation or failure.');
for (const [name, workflow] of Object.entries({ deploy, release, rollback })) {
  assert.equal(cancelInProgress(workflow), false, `${name} must serialize deliveries rather than canceling one.`);
  assert.ok(timeoutMinutes(workflow).length > 0, `${name} must bound runner-loss wait time.`);
}
assert.equal(concurrencyGroup(lifecycle), concurrencyGroup(deploy), 'Non-production lifecycle changes must queue behind deployments.');
assert.equal(cancelInProgress(lifecycle), false, 'Lifecycle runs must not cancel an active deployment.');
assert.ok(Math.max(...timeoutMinutes(lifecycle)) <= 45, 'Lifecycle operations must have a 45-minute bound.');
assert.equal(
  concurrencyGroup(release),
  concurrencyGroup(rollback),
  'Promotion and rollback must share one production concurrency group.'
);
assert.match(deployScript, /PREVIOUS_API_TASK/, 'Promotion must preserve paired rollback targets.');
assert.match(deployScript, /assert-ecs-service-task\.sh "\$ECS_CLUSTER" "\$ECS_SERVICE" "\$API_TASK_ARN"/, 'Promotion must reject ECS circuit-breaker reversion of the API.');
assert.match(deployScript, /assert-ecs-service-task\.sh "\$ECS_CLUSTER" "\$WORKER_SERVICE" "\$WORKER_TASK_ARN"/, 'Promotion must reject ECS circuit-breaker reversion of the worker.');
assert.match(release, /compatibility\.rollbackAllowed/, 'Promotion must refuse incompatible rollback state.');
assert.match(rollback, /production-promotion-evidence/, 'Rollback must bind to retained promotion evidence.');
assert.doesNotMatch(rollback, /docker build/, 'Rollback must not rebuild an artifact.');
assert.match(rollbackScript, /assert-ecs-service-task\.sh "\$ECS_CLUSTER" "\$ECS_SERVICE" "\$COMPATIBLE_TASK_DEFINITION"/, 'Rollback must reject ECS reversion of the API target.');
assert.match(rollbackScript, /assert-ecs-service-task\.sh "\$ECS_CLUSTER" "\$WORKER_SERVICE" "\$COMPATIBLE_WORKER_TASK_DEFINITION"/, 'Rollback must reject ECS reversion of the worker target.');

const ciRuns = [];
scheduleWorkflow(ciRuns, {
  id: 'old-pr-sha',
  group: 'ci-Continuous integration-42',
  cancelInProgress: cancelInProgress(ci),
});
scheduleWorkflow(ciRuns, {
  id: 'new-pr-sha',
  group: 'ci-Continuous integration-42',
  cancelInProgress: cancelInProgress(ci),
});
assert.equal(ciRuns.find((run) => run.id === 'old-pr-sha').state, 'cancelled', 'Superseded CI must cancel.');
assert.equal(ciRuns.find((run) => run.id === 'new-pr-sha').state, 'active', 'Newest CI revision must run.');

const deliveryRuns = [];
scheduleWorkflow(deliveryRuns, {
  id: 'promotion',
  group: concurrencyGroup(release),
  cancelInProgress: cancelInProgress(release),
});
scheduleWorkflow(deliveryRuns, {
  id: 'duplicate-rollback',
  group: concurrencyGroup(rollback),
  cancelInProgress: cancelInProgress(rollback),
});
assert.equal(
  deliveryRuns.find((run) => run.id === 'duplicate-rollback').state,
  'queued',
  'Production rollback must queue behind an active production promotion.'
);

assert.ok(Math.max(...timeoutMinutes(release), ...timeoutMinutes(rollback)) <= 60, 'Production delivery must retain a bounded runner-loss window.');
assert.ok(Math.max(...timeoutMinutes(deploy)) <= 90, 'Non-production deployment must retain a bounded cold-start window.');
assert.match(deploy, /timeout --foreground 2100 bash scripts\/deploy-ecs-release\.sh/, 'Non-production must terminate deployment with recovery time left.');
assert.match(release, /timeout --foreground 2100 bash scripts\/deploy-ecs-release\.sh/, 'Production promotion must terminate deployment with recovery time left.');
assert.match(rollback, /timeout --foreground 1200 bash scripts\/rollback-ecs-release\.sh/, 'Standalone rollback must terminate its target attempt with restore time left.');
const databaseStartTimeout = Number(lifecycleScript.match(/timeout --foreground (\d+) aws rds wait db-instance-available/)?.[1]);
assert.ok(databaseStartTimeout > 0, 'Non-production database startup must have an explicit timeout.');
assert.ok(Math.max(...timeoutMinutes(deploy)) * 60 - databaseStartTimeout - 2100 >= 1200, 'Non-production must reserve at least 20 runner minutes after cold database startup and its deployment deadline.');
const credentialAction = 'aws-actions/configure-aws-credentials@cbe3b392738ccf3f987d68400dafcf4b0624a56c';
assert.equal(deploy.split(credentialAction).length - 1, 2, 'Non-production must refresh OIDC credentials after database startup.');
assert.ok(deploy.indexOf('scripts/manage-nonprod-lifecycle.sh start-database') < deploy.lastIndexOf(credentialAction) && deploy.lastIndexOf(credentialAction) < deploy.indexOf('scripts/deploy-ecs-release.sh'), 'The fresh OIDC session must cover deployment and rollback.');
assert.ok(Math.max(...timeoutMinutes(release)) * 60 - 2100 >= 1200, 'Production must reserve at least 20 runner minutes beyond its deployment deadline.');
assert.ok(Math.max(...timeoutMinutes(rollback)) * 60 - 1200 >= 1200, 'Standalone rollback must reserve at least 20 runner minutes to restore the captured pair.');

const manifest = {
  sourceRevision: '0123456789abcdef0123456789abcdef01234567',
  apiDigest: 'sha256:api',
  workerDigest: 'sha256:worker',
  compatibility: { rollbackAllowed: true },
};
const promotionEvidence = {
  sourceRevision: manifest.sourceRevision,
  releaseArtifactDigest: manifest.apiDigest,
  workerReleaseArtifactDigest: manifest.workerDigest,
  taskDefinition: 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-api:7',
  workerTaskDefinition: 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-worker:7',
};
assert.equal(
  canRollback(manifest, promotionEvidence, promotionEvidence.taskDefinition, promotionEvidence.workerTaskDefinition),
  true,
  'Compatible rollback must bind manifest, source, digest, and task definition.'
);
assert.equal(
  canRollback({ ...manifest, compatibility: { rollbackAllowed: false } }, promotionEvidence, promotionEvidence.taskDefinition, promotionEvidence.workerTaskDefinition),
  false,
  'Incompatible rollback state must fail closed.'
);
assert.equal(
  canRollback(manifest, promotionEvidence, 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-api:8', promotionEvidence.workerTaskDefinition),
  false,
  'Rollback to an unattributed task definition must fail closed.'
);
assert.equal(
  canRollback(manifest, promotionEvidence, promotionEvidence.taskDefinition, 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-worker:8'),
  false,
  'Rollback to an unattributed worker task definition must fail closed.'
);

console.log('Reliability fault-injection contracts passed.');
