import { strict as assert } from 'node:assert';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { gunzipSync } from 'node:zlib';
import './rendering-fixture-bootstrap.mjs';
import './rendering-idempotency.mjs';
import './rendering-http2.mjs';
import './rendering-operator.mjs';
import './native-renderer-diagnostics.mjs';
import './corpus-additional-references.mjs';
import './regional-client.mjs';
import './regional-invoker.mjs';
import './regional-release-evidence.mjs';
import './regional-deployment.mjs';

const workflows = Object.fromEntries(['build-release.yml', 'deploy-nonprod.yml', 'nonprod-lifecycle.yml', 'release.yml', 'rollback.yml'].map((file) => [file, readFileSync(`.github/workflows/${file}`, 'utf8')]));
const deployScript = readFileSync('scripts/deploy-ecs-release.sh', 'utf8');
const renderingCorpus = readFileSync('tests/rendering/corpus/run-native.sh', 'utf8');
const configureNonprod = readFileSync('scripts/configure-nonprod-github.ps1', 'utf8');
const migrationScript = readFileSync('scripts/run-ecs-migration.sh', 'utf8');
const rollbackScript = readFileSync('scripts/rollback-ecs-release.sh', 'utf8');
const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
const immutableAction = /uses: [\w-]+\/[\w-]+@[a-f0-9]{40} # v[\d.]+/g;

const pixelWorkspace = mkdtempSync(join(tmpdir(), 'rendering-pixels-'));
try {
  const pixels = 'historical-v1.pfm';
  for (const [variant, reference] of [
    ['primary', readFileSync(`tests/rendering/corpus/reference/${pixels}`)],
    ['intel', gunzipSync(readFileSync('tests/rendering/corpus/reference/intel-historical-v1.pfm.gz'))],
  ]) {
    assert.equal(reference.toString('ascii', 0, 11), 'PF\n512 341\n');
    const pixelCheck = renderingCorpus.slice(renderingCorpus.indexOf('  test -s "$workspace/$pixels"'), renderingCorpus.indexOf('  output_hash='));
    for (const corruption of ['none', 'byte', 'nan', 'infinity', 'truncated', 'dimensions', 'header']) {
      let candidate = Buffer.from(reference);
      if (corruption === 'byte') candidate[candidate.length - 1] ^= 1;
      if (corruption === 'nan') candidate.writeFloatLE(NaN, candidate.length - 4);
      if (corruption === 'infinity') candidate.writeFloatLE(Infinity, candidate.length - 4);
      if (corruption === 'truncated') candidate = candidate.subarray(0, -1);
      if (corruption === 'dimensions') candidate.write('513', 3, 'ascii');
      if (corruption === 'header') candidate[0] = 88;
      writeFileSync(join(pixelWorkspace, pixels), candidate);
      const checked = spawnSync(bash, ['-c', `set -e; ${pixelCheck}`], { encoding: 'utf8',
        env: { ...process.env, workspace: pixelWorkspace.replaceAll('\\', '/'), root: resolve('tests/rendering/corpus').replaceAll('\\', '/'), pixels, size: '512',
          DEPLOYMENT_ENVIRONMENT: 'nonprod',
          DARKTABLE_FAILURE_DIR: corruption === 'byte' ? join(pixelWorkspace, 'mismatch-diagnostics').replaceAll('\\', '/') : '',
          DARKTABLE_IMAGE: `registry.invalid/renderer@sha256:${'a'.repeat(64)}` } });
      assert.equal(checked.status, corruption === 'none' ? 0 : 1, checked.stdout + checked.stderr);
      if (corruption === 'byte') {
        assert.match(checked.stderr, /Pixel reference mismatch: historical-v1\.pfm/);
        const diagnostic = JSON.parse(readFileSync(join(pixelWorkspace, 'mismatch-diagnostics', 'mismatch.json')));
        assert.match(diagnostic.status, /^FAILED/);
        assert.equal(diagnostic.referenceSHA256, '9a36b8507a7e6c6186e9ea85b168eaeb57e1eb4faa6ed0aa416c18617dd45e80');
        assert.equal(diagnostic.additionalReferenceSHA256, '1d370508d24a73c406cced938ed83b7bd64ee0207d61dc16458bcb5d49b58ed6');
        assert.equal(diagnostic.firstDifferentByte, variant === 'primary' ? candidate.length - 1 : 204);
        assert.equal(diagnostic.file, pixels);
        assert.equal(diagnostic.candidateBytes, candidate.length);
        assert.notEqual(diagnostic.candidateSHA256, diagnostic.referenceSHA256);
        assert.deepEqual(readFileSync(join(pixelWorkspace, 'mismatch-diagnostics', pixels)), candidate);
        assert.ok(diagnostic.host.availableParallelism > 0);
      }
      if (corruption === 'nan' || corruption === 'infinity') assert.match(checked.stderr, /Non-finite PFM pixel/);
      if (corruption === 'truncated') assert.match(checked.stderr, /PFM pixel length mismatch/);
      if (corruption === 'dimensions') assert.match(checked.stderr, /PFM exceeds its size bound/);
      if (corruption === 'header') assert.match(checked.stderr, /Expected a three-channel PFM/);
    }
    if (variant === 'intel') {
      writeFileSync(join(pixelWorkspace, pixels), reference);
      for (const scope of ['production', '']) {
        const rejected = spawnSync(bash, ['-c', `set -e; ${pixelCheck}`], { encoding: 'utf8',
          env: { ...process.env, workspace: pixelWorkspace.replaceAll('\\', '/'), root: resolve('tests/rendering/corpus').replaceAll('\\', '/'),
            pixels, size: '512', DEPLOYMENT_ENVIRONMENT: scope, DARKTABLE_FAILURE_DIR: '' } });
        assert.equal(rejected.status, 1, 'Additional references require explicit nonprod scope.');
        assert.match(rejected.stderr, /Pixel reference mismatch/);
      }
    }
  }
} finally {
  rmSync(pixelWorkspace, { recursive: true, force: true });
}

assert.match(workflows['deploy-nonprod.yml'], /bootstrap_rendering_fixtures:[\s\S]*?type: boolean[\s\S]*?default: false/);
assert.ok(!workflows['release.yml'].includes('BOOTSTRAP_RENDERING_FIXTURES'));
assert.ok(deployScript.indexOf('scripts/bootstrap-rendering-fixtures.mjs') < deployScript.indexOf('scripts/smoke-rendering.mjs'));
for (const environment of ['production', '']) {
  const rejected = spawnSync(bash, ['scripts/deploy-ecs-release.sh'], { encoding: 'utf8',
    env: { ...process.env, BOOTSTRAP_RENDERING_FIXTURES: 'true', DEPLOYMENT_ENVIRONMENT: environment } });
  assert.notEqual(rejected.status, 0);
  assert.match(rejected.stdout, /restricted to nonprod/);
}

const previousReadiness = deployScript.slice(deployScript.indexOf('if test "$PREVIOUS_API_COUNT"'),
  deployScript.indexOf('if test "$PREVIOUS_RENDERER_COUNT"'));
assert.ok(previousReadiness.includes('/ready'));
for (const [api, renderer, expected] of [[0, 0, ''], [1, 0, 'probe'], [0, 1, 'probe'], [1, 1, 'probe']]) {
  const result = spawnSync(bash, ['-c', `set -e; calls=''; curl() { calls=probe; }; ${previousReadiness}\nprintf '%s' "$calls"`], {
    encoding: 'utf8', env: { ...process.env, PREVIOUS_API_COUNT: String(api), PREVIOUS_RENDERER_COUNT: String(renderer) },
  });
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.stdout, expected, `Previous-service readiness for API=${api}, renderer=${renderer}`);
}
const rejectedReadiness = spawnSync(bash, ['-c', `set -e; curl() { return 22; }; ${previousReadiness}\nexit 0`], {
  env: { ...process.env, PREVIOUS_API_COUNT: '1', PREVIOUS_RENDERER_COUNT: '0' },
});
assert.equal(rejectedReadiness.status, 22, 'An enabled previous release must still pass readiness.');

for (const [name, workflow] of Object.entries(workflows)) {
  assert.ok(workflow.match(immutableAction)?.length, `${name} must use full-SHA action pins.`);
  assert.ok(!workflow.includes('@main') && !workflow.includes('@master') && !workflow.includes('@latest'), `${name} must not use a mutable action reference.`);
  assert.ok(workflow.includes('timeout-minutes:'), `${name} must bound runner time.`);
}

assert.ok(!workflows['build-release.yml'].includes('actions/attest-build-provenance@'), 'Build workflow must not require unavailable hosted attestations.');
assert.ok(!workflows['build-release.yml'].includes('attestations: write') && !workflows['build-release.yml'].includes('id-token: write'), 'Evidence generation must retain read-only token permissions.');
assert.ok(workflows['build-release.yml'].includes('github.run_id') && workflows['build-release.yml'].includes('github.repository'), 'Local provenance must bind the release workflow invocation and repository.');
assert.ok(workflows['build-release.yml'].includes('apps/ingest-worker/Dockerfile'), 'Build workflow must package the ingest worker.');
assert.ok(workflows['build-release.yml'].includes('workflows: [Continuous integration]'), 'Build workflow must be triggered only by the CI workflow.');
assert.ok(!workflows['build-release.yml'].includes('workflow_dispatch'), 'Build workflow must not self-certify manually dispatched validation evidence.');
assert.ok(workflows['build-release.yml'].includes('workflow_run.head_sha'), 'Build workflow must bind builds to the validated source revision.');
assert.ok(readFileSync('scripts/generate-release-evidence.mjs', 'utf8').includes('validation-evidence.json'), 'Build workflow evidence generator must retain CI validation evidence.');
assert.ok(workflows['deploy-nonprod.yml'].includes('environment: nonprod'), 'Non-production deployment must use its protected environment.');
assert.ok(workflows['deploy-nonprod.yml'].includes('id-token: write'), 'Non-production deployment must use short-lived OIDC credentials.');
assert.ok(workflows['deploy-nonprod.yml'].includes('verify-release-evidence.mjs'), 'Non-production deployment must reject a mismatched manifest.');
assert.ok(workflows['deploy-nonprod.yml'].includes('--require-regional'), 'Non-production deployment must require worker and regional-client artifacts.');
assert.ok(workflows['deploy-nonprod.yml'].includes('--require-regional "$GITHUB_REPOSITORY" "$RELEASE_RUN_ID"'), 'Non-production deployment must bind provenance to the trusted repository and downloaded release run.');
assert.ok(workflows['deploy-nonprod.yml'].includes('Validate deployment configuration') && workflows['deploy-nonprod.yml'].includes('Missing required deployment setting'), 'Non-production deployment must report missing protected configuration before AWS authentication.');
assert.ok(workflows['deploy-nonprod.yml'].includes('scripts/deploy-ecs-release.sh'), 'Non-production deployment must use the coordinated deployment path.');
assert.ok(workflows['deploy-nonprod.yml'].includes('AWS_NONPROD_RENDERING_ACCEPTANCE_FIXTURES') && workflows['deploy-nonprod.yml'].includes('RENDERING_ACCEPTANCE_FIXTURES'), 'Non-production deployment must require protected distinct rendering acceptance fixture identities.');
assert.ok(workflows['deploy-nonprod.yml'].includes('scripts/manage-nonprod-lifecycle.sh start-database'), 'Non-production deployment must start RDS before migration.');
const nonprodCredentialAction = 'aws-actions/configure-aws-credentials@cbe3b392738ccf3f987d68400dafcf4b0624a56c';
assert.equal(workflows['deploy-nonprod.yml'].split(nonprodCredentialAction).length - 1, 2, 'Non-production deployment must refresh its OIDC session after a cold database start.');
assert.ok(workflows['deploy-nonprod.yml'].indexOf('scripts/manage-nonprod-lifecycle.sh start-database') < workflows['deploy-nonprod.yml'].lastIndexOf(nonprodCredentialAction) && workflows['deploy-nonprod.yml'].lastIndexOf(nonprodCredentialAction) < workflows['deploy-nonprod.yml'].indexOf('scripts/deploy-ecs-release.sh'), 'Fresh deployment credentials must be acquired after database startup and before migration.');
assert.ok(workflows['deploy-nonprod.yml'].includes('AWS_NONPROD_DATABASE_INSTANCE') && configureNonprod.includes('AWS_NONPROD_DATABASE_INSTANCE = $outputs.DatabaseInstanceIdentifier'), 'Non-production deployment must consume the generated RDS identifier.');
assert.ok(workflows['deploy-nonprod.yml'].includes('RENDERER_REPOSITORY') && configureNonprod.includes('AWS_NONPROD_RENDERER_REPOSITORY = $outputs.RendererRepositoryName'), 'Non-production deployment must consume the generated renderer repository.');
assert.ok(workflows['deploy-nonprod.yml'].includes('RENDERER_SERVICE') && configureNonprod.includes('AWS_NONPROD_RENDERER_SERVICE = $outputs.RendererServiceName'), 'Non-production deployment must consume the generated renderer service.');
assert.ok(workflows['deploy-nonprod.yml'].includes('AWS_NONPROD_VERSION_CONTROL_HEARTBEAT_ALARM') && configureNonprod.includes('AWS_NONPROD_VERSION_CONTROL_HEARTBEAT_ALARM = $outputs.VersionControlHeartbeatAlarmName'), 'Non-production deployment must consume the generated version-control heartbeat alarm name.');
assert.ok(workflows['deploy-nonprod.yml'].includes('cloudwatch enable-alarm-actions --alarm-names "$VERSION_CONTROL_HEARTBEAT_ALARM"'), 'A successful non-production deployment must restore heartbeat notifications.');
const lifecycle = workflows['nonprod-lifecycle.yml'];
assert.ok(lifecycle.includes('workflow_dispatch:') && lifecycle.includes("cron: '0 17 * * *'"), 'Non-production lifecycle must support manual control and daily shutdown.');
assert.ok(lifecycle.includes('environment: nonprod') && lifecycle.includes('id-token: write'), 'Non-production lifecycle must use the protected OIDC environment.');
assert.ok(lifecycle.includes("github.event_name == 'schedule' && 'stop' || inputs.action"), 'Scheduled lifecycle runs must always stop compute.');
assert.ok(lifecycle.includes('group: deploy-nonprod') && lifecycle.includes('cancel-in-progress: false'), 'Lifecycle operations must serialize with deployment.');
assert.ok(lifecycle.includes('scripts/manage-nonprod-lifecycle.sh "$REQUESTED_ACTION"'), 'Lifecycle workflow inputs must enter the validated shared script through the environment.');
assert.ok(lifecycle.includes('AWS_NONPROD_VERSION_CONTROL_HEARTBEAT_ALARM'), 'Lifecycle operations must receive the generated version-control heartbeat alarm name.');
assert.ok(lifecycle.includes('if: ${{ always() }}') && lifecycle.includes('PRIOR_JOB_STATUS: ${{ job.status }}') && lifecycle.includes('scripts/manage-nonprod-lifecycle.sh report'), 'Lifecycle failures must preserve failure and report final resource state.');
assert.ok(deployScript.includes('describe-images') && deployScript.includes('Immutable ECR tag contains different image bytes'), 'Deployment must safely reuse an identical immutable tag and reject different image bytes.');
assert.doesNotMatch(deployScript, /local[^\n]*repository=.*target=.*\$repository/, 'Bash locals must be assigned before a later local initializer expands them under nounset.');
assert.ok(!deployScript.includes('file:///dev/stdin') && ['API_UPDATED', 'WORKER_UPDATED', 'RENDERER_UPDATED', 'MIGRATION_UPDATED'].every((name) => deployScript.includes(`--cli-input-json "$${name}"`)), 'Task registration must pass generated JSON directly instead of treating stdin as an AWS CLI input file.');
assert.ok(deployScript.includes('ingest-worker.tar') && deployScript.includes('render-worker.tar'), 'Deployment must publish the verified worker artifacts.');
assert.ok(deployScript.includes('Rendering__DeployedArtifactDigest') && deployScript.includes('RENDERER_DIGEST'), 'Deployment must bind API compatibility selection to the exact deployed renderer digest.');
assert.ok(deployScript.includes('operations/drain') && deployScript.indexOf('operations/drain') < deployScript.lastIndexOf('update-service --cluster "$ECS_CLUSTER" --service "$RENDERER_SERVICE"'), 'Deployment must pause and drain old renderer work before renderer cutover.');
assert.ok(deployScript.includes('Rendering__DeployedArtifactDigest') && readFileSync('apps/render-worker/Program.cs', 'utf8').includes('rendererDigest'), 'Workers must reject a queue envelope for another renderer digest.');
assert.ok(deployScript.includes('tests/rendering/corpus/run-native.sh') && renderingCorpus.includes('pixel_sha256') && renderingCorpus.includes('cmp --silent'), 'Deployment must block on the exact renderer pixel-regression corpus.');
assert.ok(deployScript.includes('DARKTABLE_FAILURE_DIR="$RELEASE_DIR/rendering-native-failure"'), 'Native mismatch diagnostics must have a dedicated output directory.');
assert.match(workflows['deploy-nonprod.yml'], /name: Retain failed public-corpus native comparison for diagnosis\s+if: \$\{\{ failure\(\) \}\}[\s\S]*?name: rendering-native-failure\s+path: artifacts\/release\/rendering-native-failure\s+if-no-files-found: ignore\s+retention-days: 14/);
assert.ok(deployScript.includes('operations/admit') && deployScript.includes('scripts/smoke-rendering.mjs') && deployScript.includes('operations/activate'), 'Deployment must admit a paused candidate, complete HTTP acceptance, then activate it.');
assert.ok(deployScript.indexOf('operations/admit') < deployScript.indexOf('scripts/smoke-rendering.mjs') && deployScript.indexOf('scripts/smoke-rendering.mjs') < deployScript.indexOf('operations/activate'), 'Compatibility activation must follow rather than precede the rendering HTTP fixture.');
assert.ok(deployScript.includes('operations/abort') && deployScript.indexOf('operations/abort') < deployScript.indexOf('update-service --cluster "$ECS_CLUSTER" --service "$ECS_SERVICE" --task-definition "$PREVIOUS_API_TASK"'), 'Failed acceptance must clear its candidate before ECS rollback restores the prior API.');
assert.ok(deployScript.indexOf('operations/activate') > deployScript.lastIndexOf('smoke-ingest.mjs') && deployScript.indexOf('operations/activate') > deployScript.lastIndexOf('mv "$DEPLOYMENT_RECORD.tmp" "$DEPLOYMENT_RECORD"') && deployScript.indexOf('operations/activate') < deployScript.lastIndexOf('ROLLBACK_REQUIRED=0'), 'Activation must be the final fallible release action before rollback is disabled.');
assert.ok(deployScript.includes('scripts/run-ecs-migration.sh'), 'Deployment must run the dedicated release migration task before services update.');
assert.ok(migrationScript.includes('command:["--migrate"]'), 'The migration task must invoke the release migration command.');
assert.ok(migrationScript.includes('trap cleanup_migration ERR INT TERM') && migrationScript.includes('aws ecs stop-task'), 'Migration timeout or interruption must stop the remote ECS task.');
assert.ok(deployScript.includes('previousWorkerTaskDefinition'), 'Deployment must retain the prior worker rollback target.');
assert.ok(deployScript.includes('trap rollback ERR INT TERM'), 'Deployment must establish rollback handling before either service update.');
assert.ok(deployScript.includes('--desired-count 1') && deployScript.includes('PREVIOUS_API_COUNT') && deployScript.includes('PREVIOUS_WORKER_COUNT'), 'The first deployment must start zero-count bootstrap services and restore their prior counts on failure.');
assert.ok(deployScript.includes('"$API_TASK_ARN"'), 'Deployment must verify the API service stabilized on the candidate task definition.');
assert.ok(deployScript.includes('"$WORKER_TASK_ARN"'), 'Deployment must verify the worker service stabilized on the candidate task definition.');
assert.ok(deployScript.includes('scripts/smoke-ingest.mjs'), 'Deployment must exercise Feature 005 end to end.');
assert.ok(deployScript.includes('curl --fail'), 'Deployment must fail when its health checks fail.');
assert.ok(workflows['release.yml'].includes('environment: production'), 'Production promotion must require the protected production environment.');
assert.ok(workflows['release.yml'].includes("tags: ['v*']"), 'Production promotion must be initiated by a versioned tag.');
assert.ok(workflows['release.yml'].includes('Production promotion requires a v* tag.'), 'Manual production dispatch must reject non-versioned tags.');
assert.ok(workflows['release.yml'].includes('git merge-base --is-ancestor'), 'Production promotion must require protected-default ancestry.');
assert.ok(workflows['release.yml'].includes('compatibility.rollbackAllowed'), 'Production promotion must refuse an incompatible persistent-state transition.');
assert.ok(workflows['release.yml'].includes('--require-worker "$GITHUB_REPOSITORY" "$RELEASE_RUN_ID"'), 'Production promotion must bind provenance to the trusted repository and selected release run.');
assert.ok(workflows['rollback.yml'].includes('--require-worker "$GITHUB_REPOSITORY"'), 'Rollback must bind current provenance to the trusted repository.');
assert.ok(workflows['release.yml'].includes('nonprod-deployment-evidence'), 'Production promotion must require retained non-production deployment evidence.');
assert.ok(workflows['release.yml'].includes('verify-promotion-evidence.mjs'), 'Production promotion must compare exact non-production evidence before approval.');
assert.ok(workflows['release.yml'].includes('Production renderer promotion remains blocked until T021 is complete.') && workflows['release.yml'].includes('exit 1'), 'Production promotion must fail closed until T021 is complete.');
assert.ok(workflows['release.yml'].includes('AWS_PRODUCTION_WORKER_SERVICE'), 'Production promotion must configure the worker service.');
assert.ok(workflows['release.yml'].includes('AWS_PRODUCTION_RENDERER_SERVICE'), 'Production promotion must configure the renderer service.');
assert.ok(workflows['release.yml'].includes('scripts/deploy-ecs-release.sh'), 'Production promotion must use the same coordinated migration and rollback path as non-production.');
assert.ok(workflows['rollback.yml'].includes('compatibility.rollbackAllowed'), 'Rollback must require explicit compatibility evidence.');
assert.ok(workflows['rollback.yml'].includes('compatible_task_definition'), 'Rollback must restore a named task definition without rebuilding.');
assert.ok(workflows['rollback.yml'].includes('compatible_worker_task_definition'), 'Rollback must restore the worker paired with the API release.');
assert.ok(workflows['rollback.yml'].includes('compatible_renderer_task_definition'), 'Rollback must restore the renderer paired with the API release.');
assert.ok(rollbackScript.includes('restoredWorkerTaskDefinition'), 'Rollback evidence must identify the restored worker task definition.');
assert.ok(rollbackScript.includes('restoredRendererTaskDefinition'), 'Rollback evidence must identify the restored renderer task definition.');
assert.ok(workflows['rollback.yml'].includes('production-promotion-evidence'), 'Rollback must be bound to retained healthy promotion evidence.');
assert.ok(workflows['rollback.yml'].includes('production-promotion.json'), 'Rollback must verify the requested task definition was previously promoted.');
assert.ok(!workflows['rollback.yml'].includes('docker build'), 'Rollback must never rebuild artifacts.');
assert.ok(workflows['rollback.yml'].includes('scripts/rollback-ecs-release.sh'), 'Rollback must use the bounded paired restoration path.');
assert.ok(rollbackScript.includes('"$COMPATIBLE_TASK_DEFINITION"'), 'Rollback must verify the API service stabilized on the requested task definition.');
assert.ok(rollbackScript.includes('"$COMPATIBLE_WORKER_TASK_DEFINITION"'), 'Rollback must verify the worker service stabilized on the requested task definition.');
assert.ok(rollbackScript.includes('"$COMPATIBLE_RENDERER_TASK_DEFINITION"'), 'Rollback must verify the renderer service stabilized on the requested task definition.');
assert.ok(rollbackScript.includes('curl --fail'), 'Rollback must fail when the restored release is unhealthy.');
assert.ok(!workflows['rollback.yml'].includes('"${{ inputs.'), 'Workflow-dispatch inputs must not be interpolated into shell commands.');

function assertServiceTask(actual, expected) {
  return spawnSync(
    bash,
    ['-c', 'set -Ee; rollback() { printf "paired rollback activated\\n"; }; trap rollback ERR; aws() { printf "%s\\n" "$MOCK_ACTUAL_TASK"; }; export -f aws; bash scripts/assert-ecs-service-task.sh cluster service "$MOCK_EXPECTED_TASK"'],
    { cwd: process.cwd(), encoding: 'utf8', env: { ...process.env, MOCK_ACTUAL_TASK: actual, MOCK_EXPECTED_TASK: expected } }
  );
}

const candidateTask = 'arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-api:8';
assert.equal(assertServiceTask(candidateTask, candidateTask).status, 0, 'The exact candidate task definition must pass.');
const circuitBreakerReversion = assertServiceTask('arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform-api:7', candidateTask);
assert.notEqual(circuitBreakerReversion.status, 0, 'A service stabilized on the prior task definition must fail closed.');
assert.match(circuitBreakerReversion.stderr, /unexpected task definition/, 'A circuit-breaker reversion must report the binding failure.');
assert.match(circuitBreakerReversion.stdout, /paired rollback activated/, 'A post-stability binding failure must activate the parent rollback trap.');

const timedOutMigration = spawnSync(
  bash,
  ['-c', 'aws() { if test "$2" = run-task; then printf "arn:aws:ecs:region:account:task/cluster/migration\n"; elif test "$2" = stop-task; then printf "stop-task %s\n" "$*" >&2; elif test "$2" = describe-tasks; then printf "0\n"; fi; }; jq() { printf "{}"; }; timeout() { return 124; }; export -f aws jq timeout; ECS_CLUSTER=cluster MIGRATION_TASK_DEFINITION=migration:1 ECS_MIGRATION_CONTAINER=migration MIGRATION_NETWORK="{}" bash scripts/run-ecs-migration.sh'],
  { cwd: process.cwd(), encoding: 'utf8', env: process.env }
);
assert.notEqual(timedOutMigration.status, 0, 'A migration waiter timeout must fail deployment.');
assert.match(timedOutMigration.stderr, /stop-task/, 'A migration waiter timeout must stop the remote ECS task.');
console.log('Release workflow contract tests passed.');
