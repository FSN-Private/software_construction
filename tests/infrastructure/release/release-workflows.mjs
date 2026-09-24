import { strict as assert } from 'node:assert';
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const workflows = Object.fromEntries(['build-release.yml', 'deploy-nonprod.yml', 'nonprod-lifecycle.yml', 'release.yml', 'rollback.yml'].map((file) => [file, readFileSync(`.github/workflows/${file}`, 'utf8')]));
const deployScript = readFileSync('scripts/deploy-ecs-release.sh', 'utf8');
const configureNonprod = readFileSync('scripts/configure-nonprod-github.ps1', 'utf8');
const migrationScript = readFileSync('scripts/run-ecs-migration.sh', 'utf8');
const rollbackScript = readFileSync('scripts/rollback-ecs-release.sh', 'utf8');
const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
const immutableAction = /uses: [\w-]+\/[\w-]+@[a-f0-9]{40} # v[\d.]+/g;

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
assert.ok(workflows['deploy-nonprod.yml'].includes('--require-worker'), 'Non-production deployment must reject legacy evidence without a worker artifact.');
assert.ok(workflows['deploy-nonprod.yml'].includes('--require-worker "$GITHUB_REPOSITORY" "$RELEASE_RUN_ID"'), 'Non-production deployment must bind provenance to the trusted repository and downloaded release run.');
assert.ok(workflows['deploy-nonprod.yml'].includes('Validate deployment configuration') && workflows['deploy-nonprod.yml'].includes('Missing required deployment setting'), 'Non-production deployment must report missing protected configuration before AWS authentication.');
assert.ok(workflows['deploy-nonprod.yml'].includes('scripts/deploy-ecs-release.sh'), 'Non-production deployment must use the coordinated deployment path.');
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
assert.ok(workflows['release.yml'].includes('verify-renderer-production-gate.mjs') && workflows['release.yml'].includes('AWS_PRODUCTION_RENDERER_IMAGE'), 'Production promotion must fail closed until a digest-qualified renderer scan proves the accepted findings remediated.');
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
