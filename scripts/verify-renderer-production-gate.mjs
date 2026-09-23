import { readFileSync } from 'node:fs';

const [baselinePath, candidatePath, expectedImage] = process.argv.slice(2);
const digestPattern = /^[^\s@]+@sha256:[a-f0-9]{64}$/;
const baselineDigest = 'sha256:7394b6f15adde742c7d2256060ff7383fca8d56ce62b63939516009ab2e328eb';

if (!baselinePath || !candidatePath || !digestPattern.test(expectedImage ?? '')) {
  throw new Error('Usage: verify-renderer-production-gate.mjs <accepted-baseline.sarif> <production-candidate.sarif> <repository@sha256:digest>.');
}

function scan(path) {
  let document;
  try {
    document = JSON.parse(readFileSync(path, 'utf8'));
  } catch {
    throw new Error(`Renderer vulnerability report is missing or invalid: ${path}.`);
  }
  const run = document.runs?.[0];
  const rules = run?.tool?.driver?.rules;
  const results = run?.results;
  if (!Array.isArray(rules) || !Array.isArray(results) || !run?.properties) throw new Error(`Renderer vulnerability report is incomplete: ${path}.`);
  const severityById = new Map(rules.map((rule) => {
    const severity = rule.properties?.tags?.find((tag) => /^(LOW|MEDIUM|HIGH|CRITICAL)$/.test(tag));
    return [rule.id, severity];
  }));
  if ([...severityById.values()].some((severity) => severity === undefined)) throw new Error(`Renderer vulnerability report lacks a severity tag: ${path}.`);
  if (results.some(({ ruleId }) => !severityById.has(ruleId))) throw new Error(`Renderer vulnerability report contains a result without a declared rule: ${path}.`);
  return {
    imageId: run.properties.imageID,
    imageName: run.properties.imageName,
    findings: new Set(results.map(({ ruleId }) => ruleId)),
    severityById,
  };
}

const baseline = scan(baselinePath);
if (baseline.imageId !== baselineDigest || !String(baseline.imageName).endsWith(`@${baselineDigest}`)) {
  throw new Error('Accepted renderer baseline does not identify the approved non-production digest.');
}
const accepted = [...baseline.severityById].filter(([, severity]) => severity === 'MEDIUM' || severity === 'LOW').map(([id]) => id);
const baselineCounts = accepted.reduce((counts, id) => ({ ...counts, [baseline.severityById.get(id)]: (counts[baseline.severityById.get(id)] ?? 0) + 1 }), {});
if (accepted.length !== 66 || baselineCounts.MEDIUM !== 48 || baselineCounts.LOW !== 18) {
  throw new Error('Accepted renderer baseline must contain exactly 48 medium and 18 low findings.');
}

const candidate = scan(candidatePath);
const expectedDigest = expectedImage.slice(expectedImage.indexOf('@') + 1);
if (candidate.imageName !== expectedImage || candidate.imageId !== expectedDigest) {
  throw new Error('Production renderer report is not bound to the configured immutable image digest.');
}
if (candidate.imageId === baselineDigest) throw new Error('The non-production-approved renderer digest is never eligible for production.');
const criticalOrHigh = [...candidate.findings].filter((id) => ['CRITICAL', 'HIGH'].includes(candidate.severityById.get(id)));
if (criticalOrHigh.length) throw new Error(`Production renderer report contains critical or high findings: ${criticalOrHigh.join(', ')}.`);
const unresolved = accepted.filter((id) => candidate.findings.has(id));
if (unresolved.length) throw new Error(`Production renderer report retains findings accepted only for non-production: ${unresolved.join(', ')}.`);

console.log(`Production renderer gate passed for ${expectedImage}; all 66 non-production-accepted findings are absent.`);
