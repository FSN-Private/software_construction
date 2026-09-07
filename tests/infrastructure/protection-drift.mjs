import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import { detectDrift, fetchCurrentProtection } from '../../packages/infrastructure/identity/verify-protection.mjs';

const baseline = new URL('../../packages/infrastructure/identity/protection-baseline.json', import.meta.url);
const observed = JSON.parse(readFileSync(baseline, 'utf8'));
const changed = {
  ...observed,
  branchProtection: { ...observed.branchProtection, allowForcePushes: true },
};

assert.deepEqual(detectDrift(observed, observed), []);
assert.deepEqual(detectDrift(observed, changed), ['branch protection allowForcePushes expected false, found true']);

const current = await fetchCurrentProtection(observed, 'test-token', async (url) => {
  const path = new URL(url).pathname;
  const responses = {
    '/repos/FSN-Private/software_construction/rules/branches/master': [
      { type: 'deletion' },
      { type: 'non_fast_forward' },
      { type: 'required_linear_history' },
      {
        type: 'required_status_checks',
        parameters: {
          strict_required_status_checks_policy: true,
          required_status_checks: observed.requiredChecks.map((context) => ({ context })),
        },
      },
    ],
    '/repos/FSN-Private/software_construction/environments/nonprod': {
      deployment_branch_policy: { protected_branches: true },
      protection_rules: [],
    },
    '/repos/FSN-Private/software_construction/environments/production': {
      deployment_branch_policy: { protected_branches: true },
      protection_rules: [{ type: 'required_reviewers', reviewers: [{ type: 'User', id: 1 }] }],
    },
  };
  return { ok: true, status: 200, json: async () => responses[path] };
});
assert.deepEqual(detectDrift(observed, current), []);
assert.deepEqual(detectDrift(observed, { ...current, requiredChecks: current.requiredChecks.slice(1) }), ['missing required check: ci / backend']);

console.log('Protection evidence drift tests passed.');
