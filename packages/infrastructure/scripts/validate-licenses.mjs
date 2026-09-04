import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const allowed = new Set(['Apache-2.0', 'MIT', 'ISC', 'BSD-2-Clause', 'BSD-3-Clause', 'MPL-2.0', 'BlueOak-1.0.0', '(MIT OR GPL-3.0-or-later)']);
const lock = JSON.parse(readFileSync('package-lock.json', 'utf8'));
const invalid = [];
for (const path of Object.keys(lock.packages).filter((candidate) => candidate.startsWith('node_modules/'))) {
  const manifestPath = join(path, 'package.json');
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  if (!allowed.has(manifest.license)) invalid.push(`${manifest.name ?? path}: ${manifest.license ?? 'missing license'}`);
}

if (invalid.length > 0) throw new Error(`Unapproved dependency licenses:\n- ${invalid.join('\n- ')}`);
console.log('Infrastructure dependency licenses are approved.');
