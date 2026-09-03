import { readFileSync } from 'node:fs';

export function detectDrift(expected, actual) {
  const drift = [];
for (const check of expected.requiredChecks) {
  if (!actual.requiredChecks?.includes(check)) {
    drift.push(`missing required check: ${check}`);
  }
}
for (const [key, value] of Object.entries(expected.branchProtection)) {
  if (actual.branchProtection?.[key] !== value) {
    drift.push(`branch protection ${key} expected ${value}, found ${actual.branchProtection?.[key]}`);
  }
}
for (const [environment, expectedSettings] of Object.entries(expected.environments)) {
  for (const [key, value] of Object.entries(expectedSettings)) {
    if (actual.environments?.[environment]?.[key] !== value) {
      drift.push(`${environment} protection ${key} expected ${value}, found ${actual.environments?.[environment]?.[key]}`);
    }
  }
}

  return drift;
}

if (import.meta.main) {
  const [expectedPath, actualPath] = process.argv.slice(2);
  if (!expectedPath || !actualPath) {
    throw new Error('Usage: node verify-protection.mjs <baseline.json> <observed-settings.json>.');
  }

  const expected = JSON.parse(readFileSync(expectedPath, 'utf8'));
  const actual = JSON.parse(readFileSync(actualPath, 'utf8'));
  const drift = detectDrift(expected, actual);
  if (drift.length > 0) {
    throw new Error(`Protection drift detected:\n- ${drift.join('\n- ')}`);
  }

  console.log('Repository and environment protection evidence matches the v1 baseline.');
}
