import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const directory = mkdtempSync(join(tmpdir(), 'regional-rollback-'));
const workflow = readFileSync('.github/workflows/deploy-nonprod.yml', 'utf8');
assert.ok(workflow.includes("always() && steps.deploy_release.outcome == 'success' && (steps.acceptance_evidence.outcome != 'success' || steps.activate_release.outcome != 'success' || steps.deployment_evidence.outcome != 'success')"));
assert.ok(workflow.indexOf('rendering-deployment-control.mjs activate') > workflow.indexOf('name: nonprod-acceptance-evidence'));
assert.ok(workflow.indexOf('name: nonprod-deployment-evidence') > workflow.indexOf('rendering-deployment-control.mjs activate'));
assert.ok(workflow.includes('id: deployment_evidence') && workflow.includes('if-no-files-found: error'));
assert.ok(workflow.indexOf('scripts/rollback-unqualified-nonprod.sh') > workflow.indexOf('name: nonprod-deployment-evidence'));
const task = version => `arn:aws:ecs:ap-southeast-1:111122223333:task-definition/platform:${version}`;
const record = { sourceRevision: 'a'.repeat(40), taskDefinition: task(10), workerTaskDefinition: task(11), rendererTaskDefinition: task(12),
  previousTaskDefinition: task(1), previousWorkerTaskDefinition: task(2), previousRendererTaskDefinition: task(3),
  previousApiDesiredCount: 0, previousWorkerDesiredCount: 2, previousRendererDesiredCount: 0 };
const prefix = `aws() {
  local service='' task='' count='' query='' prior=''
  for arg in "$@"; do
    case "$prior" in --services|--service) service="$arg";; --task-definition) task="$arg";; --desired-count) count="$arg";; --query) query="$arg";; esac
    prior="$arg"
  done
  if test "$2" = update-service; then
    test "$COMPATIBILITY_RESTORED" = 1 || return 31
    printf '%s\\n' "$service" >> "$TRACE"
    test "$service" != "$FAIL_SERVICE" || return 13
    printf '%s' "$task" > "$FIXTURE_DIR/$service.task"
    printf '%s' "$count" > "$FIXTURE_DIR/$service.count"
  elif test "$2" = describe-services; then
    task="$(cat "$FIXTURE_DIR/$service.task")"
    count="$(cat "$FIXTURE_DIR/$service.count")"
    if test -n "$query"; then printf '%s\\n' "$task"; else
      jq -n --arg task "$task" --argjson count "$count" '{failures:[],services:[{taskDefinition:$task,desiredCount:$count,runningCount:$count,pendingCount:0}]}'
    fi
  elif test "$2" = describe-task-definition; then
    jq -n --arg digest "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" '{containerDefinitions:[{name:"api",environment:[{name:"Rendering__DeployedArtifactDigest",value:$digest}]},{name:"renderer",environment:[{name:"Rendering__DeployedArtifactDigest",value:$digest}]}]}'
  else test "$2" = wait; fi
}
node() {
  test "$1" = scripts/rendering-deployment-control.mjs || return 32
  printf 'CONTROL-%s\\n' "$2" >> "$TRACE"
  if test "$2" = rollback; then COMPATIBILITY_RESTORED=1; fi
  test "$2" = rollback || test "$2" = resume
}
timeout() { shift; "$@"; }
`;
try {
  const script = join(directory, 'exercise.sh'), trace = join(directory, 'trace'), deployment = join(directory, 'record.json');
  writeFileSync(script, prefix + readFileSync('scripts/rollback-unqualified-nonprod.sh', 'utf8').replaceAll('\r\n', '\n'));
  const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
  const run = (change = () => {}, fail = '') => {
    const value = structuredClone(record); change(value); writeFileSync(deployment, JSON.stringify(value)); writeFileSync(trace, '');
    for (const [index, service] of ['api', 'worker', 'renderer'].entries()) {
      writeFileSync(join(directory, `${service}.task`), task(index + 10)); writeFileSync(join(directory, `${service}.count`), '1');
    }
    const result = spawnSync(bash, [script], { encoding: 'utf8', env: { ...process.env, DEPLOYMENT_ENVIRONMENT: 'nonprod', AWS_REGION: 'ap-southeast-1',
      DEPLOYMENT_RECORD: deployment, SOURCE_REVISION: record.sourceRevision, ECR_REGISTRY: '111122223333.dkr.ecr.ap-southeast-1.amazonaws.com',
      ECS_CLUSTER: 'fixture', ECS_SERVICE: 'api', WORKER_SERVICE: 'worker', RENDERER_SERVICE: 'renderer', ECS_API_CONTAINER: 'api', ECS_RENDERER_CONTAINER: 'renderer', TRACE: trace, FIXTURE_DIR: directory, FAIL_SERVICE: fail } });
    const events = readFileSync(trace, 'utf8').trim().split('\n').filter(Boolean);
    return { ...result, events, updates: events.filter(event => !event.startsWith('CONTROL-')) };
  };
  const success = run(); assert.equal(success.status, 0, success.stderr); assert.deepEqual(success.updates, ['api', 'worker', 'renderer']);
  assert.deepEqual(success.events, ['CONTROL-rollback', 'api', 'worker', 'renderer'], 'Stopped prior API keeps intake closed.');
  for (const [index, service] of ['api', 'worker', 'renderer'].entries()) {
    assert.equal(readFileSync(join(directory, `${service}.task`), 'utf8'), task(index + 1));
    assert.equal(readFileSync(join(directory, `${service}.count`), 'utf8'), String([0, 2, 0][index]));
  }
  for (const change of [value => { value.sourceRevision = 'b'.repeat(40); }, value => { value.taskDefinition = task(99); },
    value => { value.previousWorkerTaskDefinition = task(2).replace('111122223333', '999988887777'); }, value => { value.previousApiDesiredCount = -1; }]) {
    const result = run(change); assert.notEqual(result.status, 0); assert.deepEqual(result.updates, [], 'Validate every rollback target before any mutation.');
  }
  const failed = run(() => {}, 'worker'); assert.notEqual(failed.status, 0); assert.deepEqual(failed.updates, ['api', 'worker', 'renderer'], 'One failed restore must not skip the remaining service.');
  const apiFailed = run(() => {}, 'api'); assert.notEqual(apiFailed.status, 0); assert.ok(!apiFailed.events.includes('CONTROL-resume'), 'Failed candidate stop must leave intake closed.');
  const running = run(value => { value.previousApiDesiredCount = 1; value.previousRendererDesiredCount = 1; }); assert.equal(running.status, 0, running.stderr);
  assert.deepEqual(running.events, ['CONTROL-rollback', 'api', 'worker', 'renderer', 'CONTROL-resume'], 'Resume only after prior API is stable and verified.');
  console.log('Post-activation evidence failure restores all previous tasks/counts and rejects stale/foreign records before writes.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir())); assert.ok(basename(directory).startsWith('regional-rollback-'));
  rmSync(directory, { recursive: true, force: true });
}
