import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const ignored = new Set(['node_modules', 'dist', 'cdk.out', '.npm-cache']);
const findings = [];
const patterns = [
  { name: 'AWS access key', expression: /AKIA[0-9A-Z]{16}/ },
  { name: 'private key', expression: /-----BEGIN (?:RSA |EC )?PRIVATE KEY-----/ },
  { name: 'GitHub token', expression: /gh[pousr]_[A-Za-z0-9]{20,}/ },
];

function scan(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (ignored.has(entry.name)) continue;
    const path = join(directory, entry.name);
    if (entry.isDirectory()) scan(path);
    if (entry.isFile() && /\.(?:json|mjs|ts|md)$/.test(entry.name)) {
      const contents = readFileSync(path, 'utf8');
      for (const pattern of patterns) {
        if (pattern.expression.test(contents)) findings.push(`${pattern.name}: ${path}`);
      }
    }
  }
}

scan('.');
if (findings.length > 0) throw new Error(`Potential secret material detected:\n- ${findings.join('\n- ')}`);
console.log('Infrastructure secret scan passed.');
