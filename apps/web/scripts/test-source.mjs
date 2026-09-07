import { readFileSync } from 'node:fs';

const source = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
const normalizedSource = source.replace(/\s+/g, ' ');
if (!source.includes('Darktable Library')) {
  throw new Error('The placeholder portal must identify the product.');
}
if (
  !source.includes('/oauth2/authorize') ||
  !source.includes('/v1/session') ||
  !source.includes('/ready') ||
  !source.includes('/v1/version')
) {
  throw new Error(
    'The owner portal must start Cognito sign-in and call every authenticated status endpoint.',
  );
}
if (
  !normalizedSource.includes('public registration is unavailable') ||
  !source.includes('Operation ID')
) {
  throw new Error(
    'The private-owner and correlated service-status boundaries must be visible.',
  );
}

console.log('Web source baseline tests passed.');
