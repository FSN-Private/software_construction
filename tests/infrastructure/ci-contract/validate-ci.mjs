import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const workflow = readFileSync('.github/workflows/ci.yml', 'utf8').replace(/\r\n/g, '\n');
const fixture = JSON.parse(readFileSync('tests/infrastructure/ci-contract/expected-checks.json', 'utf8'));

for (const check of fixture.checks) {
  assert.ok(workflow.includes(`name: ${check}`), `Missing stable check name: ${check}`);
}
assert.ok(workflow.includes('pull_request:'), 'CI must validate pull requests.');
assert.ok(workflow.includes('npm run test:ingest') && workflow.includes('compose.yml -p feature005 up -d --wait'), 'Backend CI must run the real ingest dependency suite.');
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
assert.ok(workflow.includes('aquasecurity/trivy-action@ed142fd0673e97e23eac54620cfb913e5ce36c25 # v0.36.0'), 'Security CI must pin the reviewed Trivy action.');
assert.ok(workflow.includes('scan-type: fs'), 'Security CI must scan committed dependency lockfiles without npm installs.');
assert.ok(workflow.includes('scanners: vuln'), 'Security CI must limit the required gate to vulnerability scanning.');
assert.ok(workflow.includes('severity: HIGH,CRITICAL'), 'Security CI must block high or critical dependency vulnerabilities.');
assert.ok(workflow.includes("exit-code: '1'"), 'Security CI must fail when the configured vulnerabilities are found.');
assert.ok(workflow.includes("cache: 'false'"), 'Security CI must not cache untrusted pull-request scan outputs.');
assert.ok(workflow.includes('name: ci / infrastructure\n    runs-on: ubuntu-24.04\n    timeout-minutes: 15'), 'Infrastructure CI needs sufficient time for a cold validation run.');
assert.ok(workflow.includes('name: ci / security\n    runs-on: ubuntu-24.04\n    timeout-minutes: 10'), 'Security CI needs a bounded runtime.');

const auditWorkflow = readFileSync('.github/workflows/dependency-audit.yml', 'utf8');
assert.ok(auditWorkflow.includes('workflow_dispatch:'), 'Dependency audit diagnostics must support manual investigation.');
assert.ok(auditWorkflow.includes('schedule:'), 'Dependency audit diagnostics must run on a schedule.');
assert.ok(auditWorkflow.includes('node scripts/audit-dependencies.mjs --package-lock-only'), 'Dependency audit diagnostics must audit committed lockfiles without installing dependencies.');
assert.ok(auditWorkflow.includes('timeout-minutes: 15'), 'Dependency audit diagnostics need sufficient time for bounded retries.');

for (const outcome of fixture.nonPassingOutcomes) {
  assert.notEqual(outcome, 'success', `${outcome} must be rejected by the aggregate contract.`);
}

console.log('CI check-name and fail-closed contract tests passed.');
