import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const workflow = readFileSync('.github/workflows/ci.yml', 'utf8');
const fixture = JSON.parse(readFileSync('tests/infrastructure/ci-contract/expected-checks.json', 'utf8'));

for (const check of fixture.checks) {
  assert.ok(workflow.includes(`name: ${check}`), `Missing stable check name: ${check}`);
}
assert.ok(workflow.includes('pull_request:'), 'CI must validate pull requests.');
assert.ok(workflow.includes('branches:\n      - master'), 'CI must validate protected-branch changes.');
assert.ok(!workflow.includes('pull_request_target'), 'CI must not use pull_request_target.');
assert.ok(!workflow.includes('id-token: write'), 'CI must not grant OIDC identity to pull-request jobs.');
assert.ok(workflow.includes('contents: read'), 'CI must use read-only repository content permission.');

const pins = workflow.match(/uses: [^@\s]+@[0-9a-f]{40} # v[0-9]/g) ?? [];
assert.ok(pins.length >= 2, 'CI actions must be pinned to immutable full commit identities.');
assert.ok(workflow.includes('if: ${{ always() }}'), 'The aggregate check must run after non-success outcomes.');
assert.ok(workflow.includes('test "$RESULT" = success'), 'The aggregate check must reject every non-success outcome.');
assert.ok(!workflow.includes('- run: npm run format'), 'Backend CI must not invoke web formatting without installing web dependencies.');
assert.ok(workflow.includes('cache-dependency-path: apps/web/package-lock.json'), 'Web CI must cache from its own lockfile.');
assert.ok(workflow.includes('cache-dependency-path: packages/infrastructure/package-lock.json'), 'Infrastructure CI must cache from its own lockfile.');

const protectionWorkflow = readFileSync('.github/workflows/verify-protection.yml', 'utf8');
assert.ok(protectionWorkflow.includes('PROTECTION_AUDIT_TOKEN'), 'Protection verification must use the dedicated read-only audit token.');
assert.ok(!protectionWorkflow.includes('tests/infrastructure/protection-evidence.json'), 'Protection verification must not compare two committed snapshots.');
assert.ok(workflow.includes('node scripts/audit-dependencies.mjs'), 'Security CI must use the bounded dependency-audit retry wrapper.');
assert.ok(workflow.includes('name: ci / infrastructure\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15'), 'Infrastructure CI needs sufficient time for a cold validation run.');
assert.ok(workflow.includes('name: ci / security\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15'), 'Security CI needs sufficient time for bounded retries.');

for (const outcome of fixture.nonPassingOutcomes) {
  assert.notEqual(outcome, 'success', `${outcome} must be rejected by the aggregate contract.`);
}

console.log('CI check-name and fail-closed contract tests passed.');
