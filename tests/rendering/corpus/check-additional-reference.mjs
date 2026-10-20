import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { basename } from 'node:path';
import { pathToFileURL } from 'node:url';

const approved = JSON.parse(readFileSync(new URL('./reference/intel-reference-hashes.json', import.meta.url), 'utf8'));

export function matchesAdditionalReference(name, pixels) {
  return Object.hasOwn(approved, name) && createHash('sha256').update(pixels).digest('hex') === approved[name];
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  const file = process.argv[2];
  process.exitCode = file && matchesAdditionalReference(basename(file), readFileSync(file)) ? 0 : 1;
}
