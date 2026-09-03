import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';
import { detectDrift } from '../../packages/infrastructure/identity/verify-protection.mjs';

const baseline = new URL('../../packages/infrastructure/identity/protection-baseline.json', import.meta.url);
const observed = JSON.parse(readFileSync(baseline, 'utf8'));
const changed = {
  ...observed,
  branchProtection: { ...observed.branchProtection, allowForcePushes: true },
};

assert.deepEqual(detectDrift(observed, observed), []);
assert.deepEqual(detectDrift(observed, changed), ['branch protection allowForcePushes expected false, found true']);

console.log('Protection evidence drift tests passed.');
