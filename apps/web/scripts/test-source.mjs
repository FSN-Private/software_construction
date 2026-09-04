import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
if (!source.includes('Darktable Library')) {
  throw new Error('The placeholder portal must identify the product.');
}
if (!source.includes('authenticated API integration')) {
  throw new Error(
    'The placeholder portal must not imply that authentication is already implemented.',
  );
}

console.log('Web source baseline tests passed.');
