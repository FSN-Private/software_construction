import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { gunzipSync } from 'node:zlib';
import { matchesAdditionalReference } from '../../rendering/corpus/check-additional-reference.mjs';

const capture = JSON.parse(readFileSync('docs/features/011-backup-disaster-recovery/evidence/intel-renderer-candidate-2026-10-20/candidate-manifest.json'));
const approved = JSON.parse(readFileSync('tests/rendering/corpus/reference/intel-reference-hashes.json'));
assert.equal(capture.files.length, 8);
assert.deepEqual(approved, Object.fromEntries(capture.files.map(({ file, sha256 }) => [file, sha256])), 'Only the eight exact owner-approved candidate hashes may be accepted.');
const pixels = gunzipSync(readFileSync('tests/rendering/corpus/reference/intel-historical-v1.pfm.gz'));
assert.ok(matchesAdditionalReference('historical-v1.pfm', pixels));
assert.ok(!matchesAdditionalReference('historical-v2.pfm', pixels));
assert.ok(!matchesAdditionalReference('unknown.pfm', pixels));
assert.ok(!matchesAdditionalReference('toString', pixels));
const corrupted = Buffer.from(pixels);
corrupted[corrupted.length - 1] ^= 1;
assert.ok(!matchesAdditionalReference('historical-v1.pfm', corrupted));
assert.ok(!matchesAdditionalReference('historical-v1.pfm', pixels.subarray(0, -1)));
console.log('Exact additional corpus reference acceptance and mutation rejection passed.');
