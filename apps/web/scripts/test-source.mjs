import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
if (!source.includes('Darktable Library')) {
  throw new Error('The placeholder portal must identify the product.');
}
if (!source.includes('Authenticated API integration')) {
  throw new Error(
    'The placeholder portal must not imply that authentication is already implemented.',
  );
}
if (!source.includes('public registration is unavailable') || !source.includes('Operation ID')) {
  throw new Error('The private-owner and correlated service-status boundaries must be visible.');
}

console.log('Web source baseline tests passed.');
