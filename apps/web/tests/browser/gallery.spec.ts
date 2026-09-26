import { expect, test, type Page } from '@playwright/test';

const originalSha = 'a'.repeat(64);
const xmpSha = 'b'.repeat(64);
const image =
  'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="8" height="8"%3E%3Crect width="8" height="8" fill="gray"/%3E%3C/svg%3E';
const assetOne = '10000000-0000-0000-0000-000000000001';
const assetTwo = '10000000-0000-0000-0000-000000000002';

function card(index: number) {
  return {
    assetId:
      index === 1
        ? assetOne
        : index === 2
          ? assetTwo
          : `10000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
    path: `photos/image-${index}.jpg`,
    filename: `image-${index}.jpg`,
    fileType: 'jpeg',
    capturedAt: '2024-05-06T10:00:00Z',
    camera: 'Fixture camera',
    lens: null,
    rating: 4,
    rejected: false,
    width: 640,
    height: 480,
    projectionState: 'complete',
    previewState: 'available',
    previewRequestId: `20000000-0000-0000-0000-${String(index).padStart(12, '0')}`,
  };
}

function detail(assetId: string, webVersion: number) {
  return {
    assetId,
    editId: '30000000-0000-0000-0000-000000000001',
    path: 'photos/image-1.jpg',
    fileType: 'jpeg',
    originalSha256: originalSha,
    xmpSha256: xmpSha,
    lifecycleVersion: 1,
    projectionState: 'complete',
    parseErrorCode: null,
    fields: [
      {
        name: 'title',
        text: 'Fixture title',
        number: null,
        date: null,
        boolean: null,
        source: 'xmp',
        state: 'present',
        timestampBasis: null,
      },
    ],
    working: {
      branch: 'main',
      webVersion: webVersion || null,
      webXmpSha256: webVersion ? 'c'.repeat(64) : null,
      deviceConflictCount: 0,
    },
    thumbnail: {
      state: 'available',
      requestId: '20000000-0000-0000-0000-000000000001',
      failureCode: null,
      sourceSha256: originalSha,
      xmpSha256: xmpSha,
      selectedVersion: 'fixture',
      rendererRelease: '5.6.1',
      rendererArtifactDigest: 'sha256:' + 'd'.repeat(64),
      profile: 'render-v1',
      matrixVersion: 'rendering-v1',
      completedAt: '2024-05-07T10:00:00Z',
    },
  };
}

async function fixture(
  page: Page,
  options: { performance?: boolean; previewFailed?: boolean } = {},
) {
  let preference = { hideGeolocation: true, version: 1 };
  let webVersion = 0;
  let editAttempts = 0;
  await page.route('**/platform-config.js', (route) =>
    route.fulfill({
      contentType: 'text/javascript',
      body: 'window.__PLATFORM_CONFIG__={apiBaseUrl:"http://127.0.0.1:4173",cognitoHostedUiUrl:"http://127.0.0.1:4173",cognitoClientId:"fixture",redirectUri:"http://127.0.0.1:4173",galleryLibraryId:"main"};',
    }),
  );
  await page.route('**/ready', (route) =>
    route.fulfill({ json: { status: 'ready' } }),
  );
  await page.route('**/v1/**', async (route) => {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    if (request.headers().authorization !== 'Bearer fixture-token') {
      await route.fulfill({ status: 401, json: { code: 'unauthenticated' } });
      return;
    }
    if (path === '/v1/session')
      return route.fulfill({ json: { operationId: 'op_browser' } });
    if (path === '/v1/version')
      return route.fulfill({ json: { releaseId: 'fixture' } });
    if (path === '/v1/assets') return route.fulfill({ json: [] });
    if (path === '/v1/renditions' && request.method() === 'POST')
      return route.fulfill({
        json: {
          requestId: '20000000-0000-0000-0000-000000000001',
          state: 'completed',
        },
      });
    if (path.endsWith('/gallery/preferences')) {
      if (request.method() === 'PUT')
        preference = {
          hideGeolocation: !preference.hideGeolocation,
          version: preference.version + 1,
        };
      return route.fulfill({ json: preference });
    }
    if (path.endsWith('/gallery')) {
      if (options.performance)
        await new Promise((resolve) => setTimeout(resolve, 504));
      const cards = options.performance
        ? Array.from({ length: 40 }, (_, index) => card(index + 1))
        : url.searchParams.has('cursor')
          ? [card(2)]
          : [card(1)];
      if (options.previewFailed)
        for (const item of cards) item.previewState = 'failed';
      return route.fulfill({
        json: {
          cards,
          count: options.performance ? 100_000 : 2,
          nextCursor:
            options.performance || url.searchParams.has('cursor')
              ? null
              : 'page-two',
          generation: 1,
          sort: 'captureDate',
          direction: 'desc',
          filters: {},
        },
      });
    }
    if (path.endsWith('/location'))
      return route.fulfill({ json: { latitude: 40.5, longitude: -74.25 } });
    if (path.endsWith('/metadata') && request.method() === 'PATCH') {
      editAttempts++;
      if (editAttempts === 1)
        return route.fulfill({
          status: 409,
          json: { code: 'stale_working_state' },
        });
      webVersion++;
      return route.fulfill({
        json: { workingVersion: webVersion, uncommitted: true },
      });
    }
    if (path.includes('/gallery/'))
      return route.fulfill({
        json: detail(path.split('/').at(-1)!, webVersion),
      });
    if (path.includes('/renditions/') && path.endsWith('/derivative'))
      return route.fulfill({ json: { locator: image } });
    return route.fulfill({
      status: 404,
      json: { code: 'fixture_unavailable' },
    });
  });
}

test('owner browse, paging context, privacy, details, and edit conflict', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/#id_token=fixture-token');
  await expect(page.getByRole('button', { name: /image-1.jpg/ })).toBeVisible();
  await expect(page.getByLabel('Latitude minimum')).toHaveCount(0);
  await page.getByRole('button', { name: 'Show location in gallery' }).click();
  await page.getByText('Filter images').click();
  await expect(page.getByLabel('Latitude minimum')).toBeVisible();
  await page.getByLabel('Camera', { exact: true }).fill('Fixture camera');
  await page.getByRole('button', { name: 'Apply filters' }).click();
  await expect(page).toHaveURL(/camera=Fixture\+camera/);
  await page.getByRole('button', { name: 'Next page' }).click();
  await expect(page.getByRole('button', { name: /image-2.jpg/ })).toBeVisible();
  await page.getByRole('button', { name: /image-2.jpg/ }).click();
  await expect(page.getByText('Fixture title')).toBeVisible();
  await expect(page.getByText(/No web working change/)).toBeVisible();
  await page.evaluate(() => {
    window.open = (url) => {
      document.body.dataset.previewLocator = String(url);
      return null;
    };
  });
  await page.getByRole('button', { name: 'Open full preview' }).click();
  await expect(page.locator('body')).toHaveAttribute(
    'data-preview-locator',
    image,
  );
  await page.getByRole('button', { name: 'Save working change' }).click();
  await expect(page.getByText(/working state changed/)).toBeVisible();
  await page.getByRole('button', { name: 'Save working change' }).click();
  await expect(page.getByText(/uncommitted working change/)).toBeVisible();
  await page.getByRole('button', { name: 'Back to results' }).click();
  await expect(page.getByRole('button', { name: /image-2.jpg/ })).toBeVisible();
  await expect(page).toHaveURL(/cursor=page-two/);
});

test('failed preview retains a visible status instead of an image', async ({
  page,
}) => {
  await fixture(page, { previewFailed: true });
  await page.goto('/#id_token=fixture-token');
  await expect(page.getByText('Thumbnail failed')).toBeVisible();
  await expect(page.locator('.gallery-grid img')).toHaveCount(0);
});

test('unsigned browser session cannot load gallery', async ({ page }) => {
  await fixture(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Gallery' })).toHaveCount(0);
});

test('reference page and cached thumbnails meet browser-side timing', async ({
  page,
}) => {
  test.setTimeout(120_000);
  await fixture(page, { performance: true });
  const samples: number[] = [];
  const thumbnailSamples: number[] = [];
  for (let run = 0; run < 20; run++) {
    const started = Date.now();
    await page.goto(
      `/?sort=captureDate&direction=${run % 2 ? 'asc' : 'desc'}#id_token=fixture-token`,
    );
    await expect(page.getByText(/100[\s,]000 matching images/)).toBeVisible();
    const usable = Date.now();
    await expect(page.locator('.gallery-grid img')).toHaveCount(40);
    const firstThumbnail = page.locator('.gallery-grid img').first();
    await firstThumbnail.scrollIntoViewIfNeeded();
    await expect
      .poll(() =>
        firstThumbnail.evaluate(
          (img: HTMLImageElement) => img.complete && img.naturalWidth > 0,
        ),
      )
      .toBe(true);
    if (run >= 5) {
      samples.push(usable - started);
      thumbnailSamples.push(Date.now() - usable);
    }
  }
  samples.sort((a, b) => a - b);
  thumbnailSamples.sort((a, b) => a - b);
  const p95 = (values: number[]) => values[Math.ceil(values.length * 0.95) - 1];
  console.log(
    `${test.info().project.name}: usable page p95=${p95(samples)} ms, first visible cached thumbnail p95=${p95(thumbnailSamples)} ms`,
  );
  expect(p95(samples)).toBeLessThan(2_000);
  expect(p95(thumbnailSamples)).toBeLessThan(500);
});

test('live-reference 100,000-asset page and cached renditions', async ({
  page,
}) => {
  const api = process.env.GALLERY_LIVE_API;
  test.skip(!api, 'Requires the disposable PostgreSQL/S3 reference host');
  test.setTimeout(180_000);
  let cachedImageSuccess = false;
  page.on('response', (response) => {
    if (
      new URL(response.url()).pathname.includes('/gallery-capacity/') &&
      response.status() === 200
    )
      cachedImageSuccess = true;
  });
  await page.route('**/platform-config.js', (route) =>
    route.fulfill({
      contentType: 'text/javascript',
      body: 'window.__PLATFORM_CONFIG__={apiBaseUrl:"http://127.0.0.1:4173",cognitoHostedUiUrl:"http://127.0.0.1:4173",cognitoClientId:"fixture",redirectUri:"http://127.0.0.1:4173",galleryLibraryId:"capacity"};',
    }),
  );
  await page.route('**/ready', (route) =>
    route.fulfill({ json: { status: 'ready' } }),
  );
  await page.route('**/v1/**', async (route) => {
    const url = new URL(route.request().url());
    if (
      url.pathname === '/v1/libraries/capacity/gallery' ||
      url.pathname.endsWith('/derivative')
    ) {
      const response = await route.fetch({
        url: `${api}${url.pathname}${url.search}`,
      });
      return route.fulfill({ response });
    }
    if (url.pathname === '/v1/session')
      return route.fulfill({ json: { operationId: 'op_live' } });
    if (url.pathname === '/v1/version')
      return route.fulfill({ json: { releaseId: 'reference' } });
    if (url.pathname === '/v1/assets') return route.fulfill({ json: [] });
    if (url.pathname.endsWith('/preferences'))
      return route.fulfill({ json: { hideGeolocation: true, version: 1 } });
    return route.fulfill({
      status: 404,
      json: { code: 'fixture_unavailable' },
    });
  });

  const pages: number[] = [];
  for (let run = 0; run < 30; run++) {
    await page.goto('about:blank');
    const started = Date.now();
    await page.goto('/?rating=4&limit=40#id_token=fixture-token');
    await expect(page.getByText(/16[\s,]667 matching images/)).toBeVisible();
    await expect(page.locator('.gallery-grid li')).toHaveCount(40);
    if (run >= 5) pages.push(Date.now() - started);
  }
  await expect(page.locator('.gallery-grid img')).toHaveCount(40);
  const thumbnails: number[] = [];
  for (const thumbnail of await page.locator('.gallery-grid img').all()) {
    const started = Date.now();
    await thumbnail.scrollIntoViewIfNeeded();
    await expect
      .poll(() =>
        thumbnail.evaluate(
          (img: HTMLImageElement) => img.complete && img.naturalWidth > 0,
        ),
      )
      .toBe(true);
    thumbnails.push(Date.now() - started);
  }
  const p95 = (values: number[]) =>
    values.sort((a, b) => a - b)[Math.ceil(values.length * 0.95) - 1];
  const pageP95 = p95(pages);
  const thumbnailP95 = p95(thumbnails);
  expect(cachedImageSuccess).toBe(true);
  console.log(
    `${test.info().project.name}: live 100k usable page p95=${pageP95} ms (${pages.length} measured), 40 cached S3 thumbnails p95=${thumbnailP95} ms`,
  );
  console.log(
    `${test.info().project.name}: sorted page samples=${pages.join(',')} ms`,
  );
  expect(pageP95).toBeLessThan(2_000);
  expect(thumbnailP95).toBeLessThan(500);
});
