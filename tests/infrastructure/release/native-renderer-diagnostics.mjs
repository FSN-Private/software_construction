import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';

const workspace = mkdtempSync(join(tmpdir(), 'native-diagnostics-'));
const bash = process.platform === 'win32' ? join(process.env.ProgramFiles ?? 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
const workflow = readFileSync('.github/workflows/native-renderer-diagnostics.yml', 'utf8');
assert.match(workflow, /run-id: \$\{\{ env\.RELEASE_RUN_ID \}\}/, 'Artifact download must use the same effective release run as metadata/provenance verification on push and dispatch.');
assert.ok(workflow.includes('actions/runs/$RELEASE_RUN_ID') && workflow.includes('"$GITHUB_REPOSITORY" "$RELEASE_RUN_ID"'));
try {
  mkdirSync(join(workspace, 'bin'));
  mkdirSync(join(workspace, 'tests/rendering/corpus'), { recursive: true });
  mkdirSync(join(workspace, 'scripts'));
  writeFileSync(join(workspace, 'diagnose.sh'), readFileSync('scripts/diagnose-native-renderer.sh'));
  writeFileSync(join(workspace, 'bin/docker'), `#!/usr/bin/env bash
printf '%s|%s\\n' "\${NATIVE_VARIANT:-setup}" "$*" >> calls.txt
if test "$1" = image; then printf 'sha256:diagnostic-fixture\\n'; fi
`, { mode: 0o755 });
  writeFileSync(join(workspace, 'tests/rendering/corpus/run-native.sh'), `#!/usr/bin/env bash
docker run --rm renderer
test "\${ALL_PASS:-0}" = 1 || test "$NATIVE_VARIANT" = libc-baseline
`);
  writeFileSync(join(workspace, 'scripts/collect-native-renderer-candidate.sh'), `#!/usr/bin/env bash
printf '%s' "$NATIVE_VARIANT" > collector-called.txt
`);
  const command = process.platform === 'win32'
    ? 'export PATH="$(cygpath -u "$DIAGNOSTIC_BIN"):$PATH"; bash diagnose.sh'
    : 'export PATH="$DIAGNOSTIC_BIN:$PATH"; bash diagnose.sh';
  const run = (extra = {}) => spawnSync(bash, ['-c', command], {
    cwd: workspace, encoding: 'utf8',
    env: { ...process.env, DIAGNOSTIC_BIN: join(workspace, 'bin'), SOURCE_REVISION: 'a'.repeat(40), ...extra },
  });
  const failed = run();
  assert.equal(failed.status, 1, failed.stderr);
  const calls = readFileSync(join(workspace, 'calls.txt'), 'utf8');
  assert.match(calls, /baseline\|run --rm renderer/);
  assert.match(calls, /baseline-repeat\|run --rm renderer/);
  assert.match(calls, /libc-baseline\|run --env GLIBC_TUNABLES=.*-SSE4_1.* --rm renderer/);
  assert.equal(readFileSync(join(workspace, 'artifacts/native-renderer-diagnostics/results.txt'), 'utf8'), 'baseline FAIL\nbaseline-repeat FAIL\nlibc-baseline PASS\n');
  assert.equal(readFileSync(join(workspace, 'collector-called.txt'), 'utf8'), 'baseline', 'Candidate collection must use baseline arithmetic rather than the last experiment.');
  rmSync(join(workspace, 'collector-called.txt'));
  const passed = run({ ALL_PASS: '1' });
  assert.equal(passed.status, 0, passed.stderr);
  assert.throws(() => readFileSync(join(workspace, 'collector-called.txt')));
  assert.equal(run({ SOURCE_REVISION: 'invalid' }).status, 2);
} finally {
  rmSync(workspace, { recursive: true, force: true });
}
console.log('Native renderer diagnostic dispatch and failure preservation passed.');
