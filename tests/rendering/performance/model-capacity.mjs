import { strict as assert } from 'node:assert';
import { readFileSync } from 'node:fs';

const evidence = JSON.parse(readFileSync('docs/features/008-darktable-rendering/evidence/modeled-capacity.json', 'utf8'));
const samples = evidence.nativeThumbnailMilliseconds.toSorted((left, right) => left - right);
const p95 = samples[Math.ceil(samples.length * 0.95) - 1];
const requiredWorkers = Math.ceil(evidence.referenceImageCount * evidence.freshnessRatio * p95 / 1000 / evidence.deadlineSeconds);

assert.equal(p95, evidence.nativeThumbnailP95Milliseconds, 'The recorded p95 must be calculated from the native renderer sample.');
assert.equal(requiredWorkers, evidence.requiredSingleConcurrencyWorkers, 'The freshness worker count must be derived from the measured p95.');
assert.ok(p95 <= 30_000, 'The measured native render must satisfy the uncached finish target.');
assert.equal(evidence.referenceImageCount, 100_000, 'The model must retain the reference workload.');
assert.equal(evidence.freshnessRatio, 0.99, 'The model must retain the 99% freshness target.');
assert.equal(evidence.deadlineSeconds, 300, 'The model must retain the five-minute freshness target.');
console.log(`Modeled capacity: ${p95} ms p95, ${requiredWorkers} single-concurrency workers required.`);
