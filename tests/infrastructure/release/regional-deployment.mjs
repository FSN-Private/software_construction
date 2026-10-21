import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';

const directory = mkdtempSync(join(tmpdir(), 'regional-deploy-'));
const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
const deploy = readFileSync('scripts/deploy-ecs-release.sh', 'utf8').replaceAll('\r\n', '\n');
const workflow = readFileSync('.github/workflows/deploy-nonprod.yml', 'utf8');
const build = readFileSync('.github/workflows/build-release.yml', 'utf8');
assert.ok(build.indexOf('scripts/package-regional-acceptance.ps1') < build.indexOf('docker build --file apps/api'));
assert.ok(workflow.includes('--require-regional') && workflow.includes('AWS_NONPROD_ACCEPTANCE_FUNCTION_VERSION') && workflow.includes('node-version-file: .nvmrc'));
assert.ok(workflow.indexOf('--verify-client') > workflow.indexOf('role-session-name: nonprod-start-') && workflow.indexOf('--verify-client') < workflow.indexOf('- name: Start the non-production database'));
assert.ok(deploy.lastIndexOf('scripts/invoke-regional-acceptance.mjs') > deploy.indexOf('operations/admit'));
assert.ok(deploy.indexOf('scripts/invoke-regional-acceptance.mjs --verify-client') < deploy.indexOf('RENDER_OPERATION_ID='));
const tail = 'ACCEPTANCE_ACTIVE=1\n' + deploy.slice(deploy.lastIndexOf('\nif test ', deploy.indexOf('timeout 360 env')));
assert.ok(tail.includes('regional-acceptance.json') && tail.indexOf('regional-acceptance.json') < tail.indexOf('operations/activate'));
const prefix = `set -Eeuo pipefail
rollback() { printf 'ROLLBACK\\n' >> "$TRACE"; }
trap rollback ERR
timeout() { shift; "$@"; }
env() { while test "$#" -gt 0 && [[ "$1" == *=* ]]; do export "$1"; shift; done; "$@"; }
node() {
  printf '%s\\n' "$*" >> "$TRACE"
  if test "$1" = scripts/invoke-regional-acceptance.mjs; then
    if test "$SCENARIO" = regional-failure; then return 19; fi
    printf '{}\\n' > "$RELEASE_DIR/rendering-smoke.json"
    printf '{}\\n' > "$RELEASE_DIR/ingest-smoke.json"
    if test "$SCENARIO" != missing-proof; then printf '{}\\n' > "$RELEASE_DIR/regional-acceptance.json"; fi
  else printf '{}\\n'; fi
}
curl() { printf '%s\\n' "$*" >> "$TRACE"; }
`;
try {
  for (const [environment, scenario, success] of [['nonprod', 'success', true], ['nonprod', 'regional-failure', false], ['nonprod', 'missing-proof', false], ['production', 'success', true]]) {
    for (const name of ['regional-acceptance.json', 'rendering-smoke.json', 'ingest-smoke.json']) rmSync(join(directory, name), { force: true });
    const trace = join(directory, 'trace.txt'), record = join(directory, 'deployment.json');
    writeFileSync(trace, ''); writeFileSync(record, '{}');
    const result = spawnSync(bash, ['-c', prefix + tail], { encoding: 'utf8', env: { ...process.env,
      DEPLOYMENT_ENVIRONMENT: environment, SCENARIO: scenario, TRACE: trace.replaceAll('\\', '/'), RELEASE_DIR: directory.replaceAll('\\', '/'),
      DEPLOYMENT_RECORD: record.replaceAll('\\', '/'), ID_TOKEN: 'fixture-token', RENDERING_ACCEPTANCE_FIXTURES: '[]', HEALTH_URL: 'https://fixture.invalid', RENDER_OPERATION_ID: 'fixture-operation' } });
    assert.equal(result.status === 0, success, `${environment}/${scenario}: ${result.stderr}`);
    const calls = readFileSync(trace, 'utf8');
    if (success) {
      assert.equal(calls.includes('operations/activate'), environment === 'production', 'Nonprod activation must wait for workflow evidence upload.');
      assert.equal(calls.includes('ROLLBACK'), false);
    } else {
      assert.ok(calls.includes('ROLLBACK'));
      assert.equal(calls.includes('operations/activate'), false, 'Regional failure or missing proof must prevent activation.');
    }
    assert.equal(calls.includes('scripts/invoke-regional-acceptance.mjs'), environment === 'nonprod');
    assert.equal(calls.includes('scripts/smoke-rendering.mjs'), environment === 'production');
    assert.equal(calls.includes('scripts/smoke-ingest.mjs'), environment === 'production');
    if (success && environment === 'nonprod') assert.deepEqual(JSON.parse(readFileSync(record, 'utf8')).regionalAcceptance, {});
  }
  const rollback = deploy.slice(deploy.indexOf('rollback() {'), deploy.indexOf('\ntrap rollback ERR INT TERM'));
  const admission = deploy.slice(deploy.indexOf('\nACCEPTANCE_ACTIVE=1\n'), deploy.indexOf('\nif test ', deploy.indexOf('\nACCEPTANCE_ACTIVE=1\n')));
  const trace = join(directory, 'ambiguous-admit.txt'); writeFileSync(trace, '');
  const exercise = `set -Eeuo pipefail
ROLLBACK_REQUIRED=1
ID_TOKEN=fixture
RENDER_OPERATION_ID=fixture
PREVIOUS_API_TASK=old-api PREVIOUS_WORKER_TASK=old-worker PREVIOUS_RENDERER_TASK=old-renderer
PREVIOUS_API_COUNT=0 PREVIOUS_WORKER_COUNT=0 PREVIOUS_RENDERER_COUNT=0
ECS_CLUSTER=fixture ECS_SERVICE=api WORKER_SERVICE=worker RENDERER_SERVICE=renderer HEALTH_URL=https://fixture.invalid
aws() { printf 'RESTORE\\n' >> "$TRACE"; }
timeout() { shift; "$@"; }
curl() {
  local last=""; for last in "$@"; do :; done
  if [[ "$last" == */admit ]]; then printf 'ADMIT_COMMITTED\\n' >> "$TRACE"; return 19; fi
  if [[ "$last" == */abort ]]; then printf 'ABORT\\n' >> "$TRACE"; fi
}
` + rollback + '\ntrap rollback ERR INT TERM\n' + admission;
  const ambiguous = spawnSync(bash, ['-c', exercise], { encoding: 'utf8', env: { ...process.env, TRACE: trace.replaceAll('\\', '/') } });
  assert.notEqual(ambiguous.status, 0);
  const admissionCalls = readFileSync(trace, 'utf8');
  assert.ok(admissionCalls.includes('ADMIT_COMMITTED') && admissionCalls.indexOf('ABORT') > admissionCalls.indexOf('ADMIT_COMMITTED') && admissionCalls.indexOf('ABORT') < admissionCalls.indexOf('RESTORE'), 'Ambiguous committed admission must abort before restoring services.');
  console.log('Actual deployment tail preserves production smokes and rolls back regional failure/missing proof before activation.');
} finally {
  assert.equal(dirname(directory), resolve(tmpdir())); assert.ok(basename(directory).startsWith('regional-deploy-'));
  rmSync(directory, { recursive: true, force: true });
}
