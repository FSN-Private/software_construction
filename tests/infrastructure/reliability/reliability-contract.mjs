import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const ci = readFileSync('.github/workflows/ci.yml', 'utf8');
const deploy = readFileSync('.github/workflows/deploy-nonprod.yml', 'utf8');
const release = readFileSync('.github/workflows/release.yml', 'utf8');
const rollback = readFileSync('.github/workflows/rollback.yml', 'utf8');

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

function canRollback(manifest, promotionEvidence, requestedTaskDefinition) {
  return (
    manifest.compatibility?.rollbackAllowed === true &&
    promotionEvidence.taskDefinition === requestedTaskDefinition &&
    promotionEvidence.sourceRevision === manifest.sourceRevision &&
    promotionEvidence.releaseArtifactDigest === manifest.apiDigest
  );
}

assert.equal(cancelInProgress(ci), true, 'Superseded CI revisions must cancel.');
assert.match(ci, /if: \$\{\{ always\(\) \}\}/, 'Required aggregation must run after cancellation or failure.');
for (const [name, workflow] of Object.entries({ deploy, release, rollback })) {
  assert.equal(cancelInProgress(workflow), false, `${name} must serialize deliveries rather than canceling one.`);
  assert.ok(timeoutMinutes(workflow).some((minutes) => minutes === 20), `${name} must bound runner-loss wait time.`);
}
assert.equal(
  concurrencyGroup(release),
  concurrencyGroup(rollback),
  'Promotion and rollback must share one production concurrency group.'
);
assert.match(release, /PREVIOUS_TASK/, 'Promotion must preserve a rollback target.');
assert.match(release, /compatibility\.rollbackAllowed/, 'Promotion must refuse incompatible rollback state.');
assert.match(rollback, /production-promotion-evidence/, 'Rollback must bind to retained promotion evidence.');
assert.doesNotMatch(rollback, /docker build/, 'Rollback must not rebuild an artifact.');

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

const worstDeliveryTimeout = Math.max(...timeoutMinutes(deploy), ...timeoutMinutes(release), ...timeoutMinutes(rollback));
assert.ok(worstDeliveryTimeout <= 20, 'Runner-loss waits must stay below the 30 minute rollback objective.');

const manifest = {
  sourceRevision: '0123456789abcdef0123456789abcdef01234567',
  apiDigest: 'sha256:api',
  compatibility: { rollbackAllowed: true },
};
const promotionEvidence = {
  sourceRevision: manifest.sourceRevision,
  releaseArtifactDigest: manifest.apiDigest,
  taskDefinition: 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-api:7',
};
assert.equal(
  canRollback(manifest, promotionEvidence, promotionEvidence.taskDefinition),
  true,
  'Compatible rollback must bind manifest, source, digest, and task definition.'
);
assert.equal(
  canRollback({ ...manifest, compatibility: { rollbackAllowed: false } }, promotionEvidence, promotionEvidence.taskDefinition),
  false,
  'Incompatible rollback state must fail closed.'
);
assert.equal(
  canRollback(manifest, promotionEvidence, 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-api:8'),
  false,
  'Rollback to an unattributed task definition must fail closed.'
);

console.log('Reliability fault-injection contracts passed.');
