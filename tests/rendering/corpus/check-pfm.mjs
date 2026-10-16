import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';

const [file, size] = process.argv.slice(2);
const data = readFileSync(file);
const header = /^(PF)\n([1-9][0-9]*) ([1-9][0-9]*)\n(-?1(?:\.0+)?)\n/.exec(data.toString('ascii', 0, 128));
assert.ok(header, 'Expected a three-channel PFM with unit scale.');
const width = Number(header[2]);
const height = Number(header[3]);
const bound = Number(size);
assert.ok(Number.isSafeInteger(bound) && bound > 0 && bound <= 2048, 'Invalid PFM size bound.');
assert.ok(width <= bound && height <= bound, 'PFM exceeds its size bound.');
assert.equal(Math.max(width, height), bound, 'PFM must reach its requested size bound.');
const offset = header[0].length;
assert.equal(data.length, offset + width * height * 3 * 4, 'PFM pixel length mismatch.');
for (let index = offset; index < data.length; index += 4) {
  const value = header[4].startsWith('-') ? data.readFloatLE(index) : data.readFloatBE(index);
  if (!Number.isFinite(value)) throw new Error(`Non-finite PFM pixel in ${file}.`);
}
