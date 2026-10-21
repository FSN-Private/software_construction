import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const directory = mkdtempSync(join(tmpdir(), 'regional-rollback-'));
const workflow = readFileSync('.github/workflows/deploy-nonprod.yml', 'utf8');
assert.ok(workflow.includes("always() && steps.deploy_release.outcome == 'success' && steps.deployment_evidence.outcome != 'success'"));
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
  else test "$2" = wait; fi
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
      ECS_CLUSTER: 'fixture', ECS_SERVICE: 'api', WORKER_SERVICE: 'worker', RENDERER_SERVICE: 'renderer', TRACE: trace, FIXTURE_DIR: directory, FAIL_SERVICE: fail } });
    return { ...result, updates: readFileSync(trace, 'utf8').trim().split('\n').filter(Boolean) };
  };
  const success = run(); assert.equal(success.status, 0, success.stderr); assert.deepEqual(success.updates, ['api', 'worker', 'renderer']);
  for (const [index, service] of ['api', 'worker', 'renderer'].entries()) {
    assert.equal(readFileSync(join(directory, `${service}.task`), 'utf8'), task(index + 1));
    assert.equal(readFileSync(join(directory, `${service}.count`), 'utf8'), String([0, 2, 0][index]));
  }
  for (const change of [value => { value.sourceRevision = 'b'.repeat(40); }, value => { value.taskDefinition = task(99); },
    value => { value.previousWorkerTaskDefinition = task(2).replace('111122223333', '999988887777'); }, value => { value.previousApiDesiredCount = -1; }]) {
    const result = run(change); assert.notEqual(result.status, 0); assert.deepEqual(result.updates, [], 'Validate every rollback target before any mutation.');
  }
  const failed = run(() => {}, 'worker'); assert.notEqual(failed.status, 0); assert.deepEqual(failed.updates, ['api', 'worker', 'renderer'], 'One failed restore must not skip the remaining service.');
  console.log('Post-activation evidence failure restores all previous tasks/counts and rejects stale/foreign records before writes.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir())); assert.ok(basename(directory).startsWith('regional-rollback-'));
  rmSync(directory, { recursive: true, force: true });
}
