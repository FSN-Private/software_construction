import { readFileSync } from 'node:fs';

async function getJson(path, token, fetchImpl) {
  const response = await fetchImpl(`https://api.github.com${path}`, {
    headers: {
      Accept: 'application/vnd.github+json',
      Authorization: `Bearer ${token}`,
      'X-GitHub-Api-Version': '2022-11-28',
    },
  });
  if (!response.ok) {
    throw new Error(`GitHub protection audit request failed for ${path} with HTTP ${response.status}.`);
  }
  return response.json();
}

function environmentSettings(environment) {
  const reviewers = environment.protection_rules?.find((rule) => rule.type === 'required_reviewers');
  return {
    requiredReviewers: (reviewers?.reviewers?.length ?? 0) > 0,
    deploymentBranchPolicy: environment.deployment_branch_policy?.protected_branches
      ? 'protected-branches'
      : 'custom-branches',
  };
}

function branchProtectionSettings(rules, rulesets) {
  const statusCheckRules = rules.filter((rule) => rule.type === 'required_status_checks');

  return {
    requiredChecks: statusCheckRules.flatMap((rule) =>
      (rule.parameters?.required_status_checks ?? []).map((check) => check.context),
    ),
    branchProtection: {
      requireBranchesToBeUpToDate: statusCheckRules.some(
        (rule) => rule.parameters?.strict_required_status_checks_policy === true,
      ),
      requireLinearHistory: rules.some((rule) => rule.type === 'required_linear_history'),
      allowForcePushes: !rules.some((rule) => rule.type === 'non_fast_forward'),
      allowDeletions: !rules.some((rule) => rule.type === 'deletion'),
      allowBypassActors: rulesets.some((ruleset) => (ruleset.bypass_actors?.length ?? 0) > 0),
    },
  };
}

export async function fetchCurrentProtection(expected, token, fetchImpl = fetch) {
  if (!token) {
    throw new Error('PROTECTION_AUDIT_TOKEN is required to read live GitHub protection settings.');
  }

  const [owner, repository] = expected.repository.split('/');
  const prefix = `/repos/${owner}/${repository}`;
  const [rules, environments] = await Promise.all([
    getJson(`${prefix}/rules/branches/${expected.branch}`, token, fetchImpl),
    Promise.all(Object.keys(expected.environments).map(async (name) => [
      name,
      environmentSettings(await getJson(`${prefix}/environments/${name}`, token, fetchImpl)),
    ])),
  ]);
  const rulesetReferences = [...new Map(rules.map((rule) => [
    `${rule.ruleset_source_type}:${rule.ruleset_id}`,
    { sourceType: rule.ruleset_source_type, source: rule.ruleset_source, id: rule.ruleset_id },
  ])).values()];

  if (rulesetReferences.length === 0 || rulesetReferences.some(({ sourceType, source, id }) =>
    sourceType !== 'Repository' || source !== expected.repository || !id,
  )) {
    throw new Error('GitHub protection audit requires active repository rulesets with inspectable bypass actors.');
  }

  const rulesets = await Promise.all(rulesetReferences.map(({ id }) =>
    getJson(`${prefix}/rulesets/${id}`, token, fetchImpl),
  ));

  return {
    repository: expected.repository,
    branch: expected.branch,
    ...branchProtectionSettings(rules, rulesets),
    environments: Object.fromEntries(environments),
  };
}

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
  const [expectedPath] = process.argv.slice(2);
  if (!expectedPath) {
    throw new Error('Usage: node verify-protection.mjs <baseline.json>.');
  }

  const expected = JSON.parse(readFileSync(expectedPath, 'utf8'));
  const actual = await fetchCurrentProtection(expected, process.env.GITHUB_TOKEN);
  const drift = detectDrift(expected, actual);
  if (drift.length > 0) {
    throw new Error(`Protection drift detected:\n- ${drift.join('\n- ')}`);
  }

  console.log('Repository and environment protection evidence matches the v1 baseline.');
}
