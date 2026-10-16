import { strict as assert } from 'node:assert';
import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const baseline = 'docs/features/008-darktable-rendering/evidence/darktable-5.6.1-ecr.sarif';
const directory = mkdtempSync(join(tmpdir(), 'renderer-production-gate-'));
const candidatePath = join(directory, 'candidate.sarif');
const image = '344531454551.dkr.ecr.ap-southeast-1.amazonaws.com/platform-production-render-worker@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';
const candidate = JSON.parse(readFileSync(baseline, 'utf8'));
candidate.runs[0].properties.imageID = image.slice(image.indexOf('@') + 1);
candidate.runs[0].properties.imageName = image;
candidate.runs[0].results = [];
candidate.runs[0].tool.driver.rules = [];
writeFileSync(candidatePath, JSON.stringify(candidate));

const command = [process.execPath, 'scripts/verify-renderer-production-gate.mjs', baseline, candidatePath, image];
execFileSync(command[0], command.slice(1), { stdio: 'inherit' });

const staleFinding = JSON.parse(readFileSync(candidatePath, 'utf8'));
staleFinding.runs[0].tool.driver.rules = [JSON.parse(readFileSync(baseline, 'utf8')).runs[0].tool.driver.rules[0]];
staleFinding.runs[0].results = [{ ruleId: staleFinding.runs[0].tool.driver.rules[0].id }];
writeFileSync(candidatePath, JSON.stringify(staleFinding));
assert.throws(() => execFileSync(command[0], command.slice(1), { stdio: 'pipe' }), /accepted only for non-production/);

const currentAcceptance = JSON.parse(readFileSync('docs/features/011-backup-disaster-recovery/evidence/portable-renderer-candidate-2026-10-16/candidate-manifest.json', 'utf8'));
const historicalIds = new Set(JSON.parse(readFileSync(baseline, 'utf8')).runs[0].tool.driver.rules.map(rule => rule.id));
for (const severity of ['MEDIUM', 'LOW']) {
  const finding = currentAcceptance.lowerSeverityFindings.find(finding => finding.severity === severity && !historicalIds.has(finding.id));
  assert.ok(finding, `Expected a newly accepted ${severity} finding outside the original baseline.`);
  staleFinding.runs[0].tool.driver.rules = [{ id: finding.id, properties: { tags: [severity] } }];
  staleFinding.runs[0].results = [{ ruleId: finding.id }];
  writeFileSync(candidatePath, JSON.stringify(staleFinding));
  assert.throws(() => execFileSync(command[0], command.slice(1), { stdio: 'pipe' }), /accepted only for non-production/);
}

const criticalFinding = JSON.parse(readFileSync(candidatePath, 'utf8'));
criticalFinding.runs[0].tool.driver.rules = [{ id: 'CVE-9999-0001', properties: { tags: ['CRITICAL'] } }];
criticalFinding.runs[0].results = [{ ruleId: 'CVE-9999-0001' }];
writeFileSync(candidatePath, JSON.stringify(criticalFinding));
assert.throws(() => execFileSync(command[0], command.slice(1), { stdio: 'pipe' }), /critical or high findings/);

const wrongDigest = JSON.parse(readFileSync(candidatePath, 'utf8'));
wrongDigest.runs[0].tool.driver.rules = [];
wrongDigest.runs[0].results = [];
wrongDigest.runs[0].properties.imageID = 'sha256:abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789';
writeFileSync(candidatePath, JSON.stringify(wrongDigest));
assert.throws(() => execFileSync(command[0], command.slice(1), { stdio: 'pipe' }), /not bound to the configured immutable image digest/);

console.log('Renderer production gate tests passed.');
