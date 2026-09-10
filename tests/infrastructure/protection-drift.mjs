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

const responses = {
  '/repos/FSN-Private/software_construction/rules/branches/master': [
    { type: 'deletion', ruleset_source_type: 'Repository', ruleset_source: observed.repository, ruleset_id: 99 },
    { type: 'non_fast_forward', ruleset_source_type: 'Repository', ruleset_source: observed.repository, ruleset_id: 99 },
    { type: 'required_linear_history', ruleset_source_type: 'Repository', ruleset_source: observed.repository, ruleset_id: 99 },
    {
      type: 'required_status_checks',
      parameters: {
        strict_required_status_checks_policy: true,
        required_status_checks: observed.requiredChecks.map((context) => ({ context })),
      },
      ruleset_source_type: 'Repository',
      ruleset_source: observed.repository,
      ruleset_id: 99,
    },
  ],
  '/repos/FSN-Private/software_construction/rulesets/99': {
    bypass_actors: [],
  },
  '/repos/FSN-Private/software_construction/environments/nonprod': {
    deployment_branch_policy: { protected_branches: true },
    protection_rules: [],
  },
  '/repos/FSN-Private/software_construction/environments/production': {
    deployment_branch_policy: { protected_branches: true },
    protection_rules: [{ type: 'required_reviewers', reviewers: [{ type: 'User', id: 1 }] }],
  },
};

function mockFetch(overrides = {}) {
  return async (url) => {
    const path = new URL(url).pathname;
    const response = overrides[path] ?? responses[path];
    return { ok: response !== undefined, status: response === undefined ? 404 : 200, json: async () => response };
  };
}

const current = await fetchCurrentProtection(observed, 'test-token', mockFetch());
assert.deepEqual(detectDrift(observed, current), []);
assert.deepEqual(detectDrift(observed, { ...current, requiredChecks: current.requiredChecks.slice(1) }), ['missing required check: ci / backend']);
assert.deepEqual(detectDrift(observed, {
  ...current,
  branchProtection: { ...current.branchProtection, allowBypassActors: true },
}), ['branch protection allowBypassActors expected false, found true']);
await assert.rejects(
  fetchCurrentProtection(observed, 'test-token', mockFetch({
    '/repos/FSN-Private/software_construction/rulesets/99': {},
  })),
  /cannot inspect ruleset bypass actors/,
);
const bypassed = await fetchCurrentProtection(observed, 'test-token', mockFetch({
  '/repos/FSN-Private/software_construction/rulesets/99': {
    bypass_actors: [{ actor_id: 1, actor_type: 'RepositoryRole', bypass_mode: 'always' }],
  },
}));
assert.deepEqual(detectDrift(observed, bypassed), ['branch protection allowBypassActors expected false, found true']);

console.log('Protection evidence drift tests passed.');
