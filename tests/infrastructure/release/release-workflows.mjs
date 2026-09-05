import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const workflows = Object.fromEntries(['build-release.yml', 'deploy-nonprod.yml', 'release.yml', 'rollback.yml'].map((file) => [file, readFileSync(`.github/workflows/${file}`, 'utf8')]));
const immutableAction = /uses: [\w-]+\/[\w-]+@[a-f0-9]{40} # v[\d.]+/g;

for (const [name, workflow] of Object.entries(workflows)) {
  assert.ok(workflow.match(immutableAction)?.length, `${name} must use full-SHA action pins.`);
  assert.ok(!workflow.includes('@main') && !workflow.includes('@master') && !workflow.includes('@latest'), `${name} must not use a mutable action reference.`);
  assert.ok(workflow.includes('timeout-minutes:'), `${name} must bound runner time.`);
}

assert.ok(workflows['build-release.yml'].includes('actions/attest-build-provenance@'), 'Build workflow must attest release evidence.');
assert.ok(workflows['build-release.yml'].includes('workflows: [Continuous integration]'), 'Build workflow must be triggered only by the CI workflow.');
assert.ok(!workflows['build-release.yml'].includes('workflow_dispatch'), 'Build workflow must not self-certify manually dispatched validation evidence.');
assert.ok(workflows['build-release.yml'].includes('workflow_run.head_sha'), 'Build workflow must bind builds to the validated source revision.');
assert.ok(readFileSync('scripts/generate-release-evidence.mjs', 'utf8').includes('validation-evidence.json'), 'Build workflow evidence generator must retain CI validation evidence.');
assert.ok(workflows['deploy-nonprod.yml'].includes('environment: nonprod'), 'Non-production deployment must use its protected environment.');
assert.ok(workflows['deploy-nonprod.yml'].includes('id-token: write'), 'Non-production deployment must use short-lived OIDC credentials.');
assert.ok(workflows['deploy-nonprod.yml'].includes('verify-release-evidence.mjs'), 'Non-production deployment must reject a mismatched manifest.');
assert.ok(workflows['deploy-nonprod.yml'].includes('RepoDigests'), 'Non-production deployment must record an immutable registry digest.');
assert.ok(workflows['deploy-nonprod.yml'].includes('curl --fail'), 'Non-production deployment must fail when its health checks fail.');
assert.ok(workflows['release.yml'].includes('environment: production'), 'Production promotion must require the protected production environment.');
assert.ok(workflows['release.yml'].includes("tags: ['v*']"), 'Production promotion must be initiated by a versioned tag.');
assert.ok(workflows['release.yml'].includes('Production promotion requires a v* tag.'), 'Manual production dispatch must reject non-versioned tags.');
assert.ok(workflows['release.yml'].includes('git merge-base --is-ancestor'), 'Production promotion must require protected-default ancestry.');
assert.ok(workflows['release.yml'].includes('compatibility.rollbackAllowed'), 'Production promotion must refuse an incompatible persistent-state transition.');
assert.ok(workflows['release.yml'].includes('nonprod-deployment-evidence'), 'Production promotion must require retained non-production deployment evidence.');
assert.ok(workflows['release.yml'].includes('verify-promotion-evidence.mjs'), 'Production promotion must compare exact non-production evidence before approval.');
assert.ok(workflows['release.yml'].includes('PREVIOUS_TASK'), 'Production promotion must retain a rollback target before updating ECS.');
assert.ok(workflows['release.yml'].includes('aws ecs update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$PREVIOUS_TASK"'), 'Failed production health must restore the prior task definition.');
assert.ok(workflows['rollback.yml'].includes('compatibility.rollbackAllowed'), 'Rollback must require explicit compatibility evidence.');
assert.ok(workflows['rollback.yml'].includes('compatible_task_definition'), 'Rollback must restore a named task definition without rebuilding.');
assert.ok(workflows['rollback.yml'].includes('production-promotion-evidence'), 'Rollback must be bound to retained healthy promotion evidence.');
assert.ok(workflows['rollback.yml'].includes('production-promotion.json'), 'Rollback must verify the requested task definition was previously promoted.');
assert.ok(!workflows['rollback.yml'].includes('docker build'), 'Rollback must never rebuild artifacts.');
assert.ok(workflows['rollback.yml'].includes('curl --fail'), 'Rollback must fail when the restored release is unhealthy.');
assert.ok(!workflows['rollback.yml'].includes('"${{ inputs.'), 'Workflow-dispatch inputs must not be interpolated into shell commands.');
console.log('Release workflow contract tests passed.');
