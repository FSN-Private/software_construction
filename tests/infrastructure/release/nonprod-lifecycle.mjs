import { strict as assert } from 'node:assert';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
const temporaryDirectory = mkdtempSync(join(tmpdir(), 'nonprod-lifecycle-'));
const logPath = join(temporaryDirectory, 'aws.log').replaceAll('\\', '/');

function run(action, databaseState = 'available', finalCount = '1', failMatch = '', timeoutExit = '', postSleepState = 'stopped') {
  writeFileSync(logPath, '');
  const command = String.raw`
    aws() {
      printf '%s\n' "$*" >> "$MOCK_AWS_LOG"
      if test -n "$MOCK_FAIL_MATCH" && [[ "$*" == *"$MOCK_FAIL_MATCH"* ]]; then return 44; fi
      if test "$1 $2 $3" = 'rds wait db-instance-stopped'; then return 2; fi
      if test "$1 $2" = 'rds describe-db-instances'; then
        if grep -q '^rds stop-db-instance' "$MOCK_AWS_LOG"; then printf 'stopped\n';
        elif grep -q '^sleep 15$' "$MOCK_AWS_LOG"; then printf '%s\n' "$MOCK_POST_SLEEP_STATE";
        elif grep -q '^rds start-db-instance' "$MOCK_AWS_LOG"; then printf 'available\n';
        else printf '%s\n' "$MOCK_RDS_STATE"; fi
      elif test "$1 $2" = 'ecs describe-services'; then
        printf '%s\n' "$MOCK_FINAL_COUNT"
      fi
    }
    timeout() {
      test "$1" = '--foreground' && shift
      shift
      if test -n "$MOCK_TIMEOUT_EXIT" && test "$1" = bash; then return "$MOCK_TIMEOUT_EXIT"; fi
      "$@"
    }
    sleep() { printf 'sleep %s\n' "$*" >> "$MOCK_AWS_LOG"; }
    export -f aws timeout sleep
    bash scripts/manage-nonprod-lifecycle.sh "$REQUESTED_ACTION"
  `;
  const result = spawnSync(bash, ['-c', command], {
    cwd: process.cwd(),
    encoding: 'utf8',
    env: {
      ...process.env,
      REQUESTED_ACTION: action,
      MOCK_AWS_LOG: logPath,
      MOCK_RDS_STATE: databaseState,
      MOCK_FINAL_COUNT: finalCount,
      MOCK_FAIL_MATCH: failMatch,
      MOCK_TIMEOUT_EXIT: timeoutExit,
      MOCK_POST_SLEEP_STATE: postSleepState,
      DATABASE_INSTANCE: 'database-1',
      ECS_CLUSTER: 'cluster-1',
      ECS_SERVICE: 'api-service',
      WORKER_SERVICE: 'worker-service',
    },
  });
  return { ...result, calls: readFileSync(logPath, 'utf8').trim().split(/\r?\n/).filter(Boolean) };
}

try {
  const coldDatabase = run('start-database', 'stopped');
  assert.equal(coldDatabase.status, 0, coldDatabase.stderr);
  assert.ok(coldDatabase.calls.some((call) => call.startsWith('rds start-db-instance')));
  assert.ok(coldDatabase.calls.some((call) => call.startsWith('rds wait db-instance-available')));

  const readyDatabase = run('start-database', 'available');
  assert.equal(readyDatabase.status, 0, readyDatabase.stderr);
  assert.ok(!readyDatabase.calls.some((call) => call.startsWith('rds start-db-instance')), 'Available RDS must not receive a duplicate start.');

  const startingDatabase = run('start-database', 'starting');
  assert.equal(startingDatabase.status, 0, startingDatabase.stderr);
  assert.ok(!startingDatabase.calls.some((call) => call.startsWith('rds start-db-instance')), 'Starting RDS must only be waited.');
  assert.ok(startingDatabase.calls.some((call) => call.startsWith('rds wait db-instance-available')));

  const started = run('start', 'stopped', '1');
  assert.equal(started.status, 0, started.stderr);
  const databaseAvailable = started.calls.findIndex((call) => call.startsWith('rds wait db-instance-available'));
  const firstServiceStart = started.calls.findIndex((call) => call.includes('ecs update-service') && call.includes('--desired-count 1'));
  assert.ok(databaseAvailable >= 0 && firstServiceStart > databaseAvailable, 'RDS must become available before ECS starts.');
  assert.equal(started.calls.filter((call) => call.includes('ecs update-service') && call.includes('--desired-count 1')).length, 2);
  assert.match(started.stdout, /action=start database=available api=1 worker=1/);

  const stopped = run('stop', 'available', '0');
  assert.equal(stopped.status, 0, stopped.stderr);
  const serviceStop = stopped.calls.findIndex((call) => call.includes('ecs update-service') && call.includes('--desired-count 0'));
  const databaseStop = stopped.calls.findIndex((call) => call.startsWith('rds stop-db-instance'));
  assert.ok(serviceStop >= 0 && databaseStop > serviceStop, 'ECS must stop before RDS.');
  assert.equal(stopped.calls.filter((call) => call.includes('ecs update-service') && call.includes('--desired-count 0')).length, 2);
  assert.match(stopped.stdout, /action=stop database=stopped api=0 worker=0/);

  const alreadyStopped = run('stop', 'stopped', '0');
  assert.equal(alreadyStopped.status, 0, alreadyStopped.stderr);
  assert.ok(!alreadyStopped.calls.some((call) => call.startsWith('rds stop-db-instance')), 'Stopped RDS must not receive a duplicate stop.');

  const stoppingDatabase = run('stop', 'stopping', '0');
  assert.equal(stoppingDatabase.status, 0, stoppingDatabase.stderr);
  assert.ok(!stoppingDatabase.calls.some((call) => call.startsWith('rds stop-db-instance')), 'Stopping RDS must only be waited.');
  assert.ok(stoppingDatabase.calls.some((call) => call === 'sleep 15'), 'Stopping RDS must be polled without relying on an unavailable AWS CLI waiter.');

  const stopTimedOut = run('stop', 'stopping', '0', '', '124');
  assert.equal(stopTimedOut.status, 124, 'The complete RDS polling subprocess must preserve timeout exit 124.');
  assert.match(stopTimedOut.stderr, /did not stop within 1500 seconds/);

  const unexpectedDuringStop = run('stop', 'stopping', '0', '', '', 'modifying');
  assert.notEqual(unexpectedDuringStop.status, 0, 'An unexpected state entered while polling must fail closed.');
  assert.match(unexpectedDuringStop.stderr, /entered unexpected state while stopping: modifying/);

  const failedUpdate = run('stop', 'available', '0', 'ecs update-service --cluster cluster-1 --service api-service');
  assert.notEqual(failedUpdate.status, 0, 'A failed ECS mutation must fail the lifecycle action.');

  const failedWait = run('start-database', 'stopped', '1', 'rds wait db-instance-available');
  assert.notEqual(failedWait.status, 0, 'A failed RDS waiter must fail the lifecycle action.');

  const failedFinalProbe = run('report', 'available', '1', 'ecs describe-services');
  assert.notEqual(failedFinalProbe.status, 0, 'A failed final probe must fail reporting.');
  assert.match(failedFinalProbe.stdout, /action=report database=available api=unknown worker=unknown/);

  const unsupportedState = run('start-database', 'backing-up');
  assert.notEqual(unsupportedState.status, 0);
  assert.match(unsupportedState.stderr, /cannot start from state: backing-up/);

  const invalidAction = run('destroy');
  assert.notEqual(invalidAction.status, 0);
  assert.match(invalidAction.stderr, /Expected start-database, start, stop, or report/);

  console.log('Non-production lifecycle tests passed.');
} finally {
  rmSync(temporaryDirectory, { recursive: true, force: true });
}
