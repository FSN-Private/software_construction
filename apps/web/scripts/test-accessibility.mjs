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
globalThis.window = { location: { search: '' } };
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
  const { Gallery } = await server.ssrLoadModule('/src/Gallery.tsx');
  const gallery = renderToStaticMarkup(
    createElement(Gallery, {
      baseUrl: 'https://example.invalid',
      token: 'test-only',
      library: 'main',
    }),
  );
  for (const label of [
    'All photos',
    'Filters',
    'Sort by',
    'Direction',
    'Search photos',
    'Grid density',
  ])
    if (!gallery.includes(label))
      throw new Error(`Missing labelled gallery control: ${label}`);
  if (
    !gallery.includes('role="status"') ||
    (!gallery.includes('role="alert"') &&
      !gallery.includes('aria-live="polite"'))
  )
    throw new Error('Gallery loading and errors must be announced.');
  console.log('Rendered gallery accessibility checks passed.');
  const { Comparison } = await server.ssrLoadModule('/src/Comparison.tsx');
  const comparison = renderToStaticMarkup(
    createElement(Comparison, {
      baseUrl: 'https://example.invalid',
      token: 'test-only',
      library: 'main',
      value: {
        targetCommitId: 'a'.repeat(64),
        sourceCommitId: 'b'.repeat(64),
        baseCommitId: null,
        targetStates: [
          {
            commitId: 'a'.repeat(64),
            assetId: '10000000-0000-0000-0000-000000000001',
            editId: '30000000-0000-0000-0000-000000000001',
            xmpSha256: 'c'.repeat(64),
            path: 'image.raw',
            lifecycle: 'active',
            previewState: 'failed',
            previewRequestId: null,
            previewProvenance: null,
          },
        ],
        sourceStates: [],
        differences: [],
      },
    }),
  );
  if (
    !comparison.includes('aria-labelledby="comparison-heading"') ||
    !comparison.includes('aria-label="Target preview"') ||
    !comparison.includes('Preview: failed') ||
    !comparison.includes('No XMP, path, or lifecycle differences')
  )
    throw new Error(
      'Comparison must retain textual and accessible preview status.',
    );
  console.log('Rendered lineage comparison accessibility checks passed.');
  const { MergeReview } = await server.ssrLoadModule('/src/MergeReview.tsx');
  const merge = renderToStaticMarkup(
    createElement(MergeReview, {
      baseUrl: 'https://example.invalid',
      token: 'test-only',
      library: 'main',
      targetBranch: 'main',
      targetHead: 'a'.repeat(64),
      onPublished: () => {},
    }),
  );
  if (
    !merge.includes('aria-labelledby="merge-review-heading"') ||
    !merge.includes('Source branch') ||
    !merge.includes('Prepare merge')
  )
    throw new Error('Merge review must expose labelled keyboard controls.');
  console.log('Rendered merge review accessibility checks passed.');
} finally {
  await server.close();
  delete globalThis.sessionStorage;
  delete globalThis.window;
}
