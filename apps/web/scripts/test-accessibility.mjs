import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('../index.html', import.meta.url), 'utf8');
const app = readFileSync(new URL('../src/App.tsx', import.meta.url), 'utf8');
if (!html.includes('<html lang="en">')) {
  throw new Error('The document language must be declared.');
}
if (!app.includes('<main>') || !app.includes('<h1>')) {
  throw new Error(
    'The placeholder portal must have a main landmark and page heading.',
  );
}
if (!app.includes('aria-labelledby') || !app.includes('role="status"') || !app.includes('<button')) {
  throw new Error('Sign-in and deployment status must expose labelled, announced controls.');
}

console.log('Web accessibility structure tests passed.');
