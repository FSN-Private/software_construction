import { readFileSync } from 'node:fs';
import { createServer } from 'vite';
import { renderToStaticMarkup } from 'react-dom/server';
import { createElement } from 'react';

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
if (
  !app.includes('aria-labelledby') ||
  !app.includes('role="status"') ||
  !app.includes('<button')
) {
  throw new Error(
    'Sign-in and deployment status must expose labelled, announced controls.',
  );
}

console.log('Web accessibility structure tests passed.');

globalThis.sessionStorage = { getItem: () => null };
const server = await createServer({
  server: { middlewareMode: true },
  appType: 'custom',
});
try {
  const { Library } = await server.ssrLoadModule('/src/Library.tsx');
  const markup = renderToStaticMarkup(
    createElement(Library, {
      baseUrl: 'https://example.invalid',
      token: 'test-only',
    }),
  );
  for (const id of ['library-heading', 'import-files', 'resume-batch']) {
    if (!markup.includes(`id="${id}"`))
      throw new Error(`Missing labelled import control: ${id}`);
  }
  if (
    !markup.includes('aria-live="polite"') ||
    !markup.includes('role="status"') ||
    !markup.includes('Show recoverable deletions')
  ) {
    throw new Error(
      'Import progress and recovery controls must expose text and accessible state.',
    );
  }
  for (const id of ['import-files', 'resume-batch']) {
    if (!markup.includes(`for="${id}"`))
      throw new Error(`Input ${id} must have an associated label.`);
  }
  console.log('Rendered import/recovery accessibility checks passed.');
} finally {
  await server.close();
  delete globalThis.sessionStorage;
}
