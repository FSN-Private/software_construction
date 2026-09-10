import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const contractsDirectory = join(import.meta.dirname, '../../packages/contracts');
const expectedFixtures = [
  'audit-event.json',
  'configuration.json',
  'error.problem.json',
  'health.json',
  'idempotency.json',
  'migration.json',
  'operation-id.json',
  'promotion.json',
  'telemetry-redaction.json',
];

const actualFixtures = readdirSync(contractsDirectory).filter((name) => name.endsWith('.json')).sort();
if (JSON.stringify(actualFixtures) !== JSON.stringify(expectedFixtures)) {
  throw new Error(`Unexpected contract fixture set: ${actualFixtures.join(', ')}`);
}

const fixtures = Object.fromEntries(actualFixtures.map((name) => [
  name,
  JSON.parse(readFileSync(join(contractsDirectory, name), 'utf8')),
]));

for (const [name, fixture] of Object.entries(fixtures)) {
  if (fixture.contractVersion !== 'v1') {
    throw new Error(`${name} must declare contractVersion v1.`);
  }
}

if (fixtures['error.problem.json'].contentType !== 'application/problem+json' || fixtures['error.problem.json'].status !== 401) {
  throw new Error('The error fixture must remain a generic v1 unauthenticated problem response.');
}
if (fixtures['operation-id.json'].header !== 'X-Operation-Id' || fixtures['idempotency.json'].header !== 'Idempotency-Key') {
  throw new Error('Operation and idempotency header names are part of the v1 contract.');
}
if (fixtures['health.json'].live.authentication !== 'none' || fixtures['health.json'].ready.authentication !== 'owner') {
  throw new Error('Health access boundaries must preserve public liveness and owner readiness.');
}
if (fixtures['promotion.json'].approvalRequired !== true || !fixtures['promotion.json'].promotionRule.includes('same-digest')) {
  throw new Error('Promotion must require approval and preserve immutable artifact identity.');
}
if (!fixtures['telemetry-redaction.json'].expectedOutput.includes('[REDACTED]')) {
  throw new Error('Telemetry redaction must be explicit.');
}

console.log(`Validated ${actualFixtures.length} versioned platform contract fixtures.`);
