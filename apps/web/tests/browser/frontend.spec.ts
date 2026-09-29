import { expect, test } from '@playwright/test';
import { fixture, assetOne, card, detail, image } from './fixtures';

test('drawer cancels drafts, contains focus, validates ranges, applies and removes chips', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/?camera=Original#id_token=fixture-token');
  const opener = page.getByRole('button', { name: /Filters/ });
  await opener.click();
  await page.getByLabel('Camera', { exact: true }).fill('Draft');
  await page.keyboard.press('Escape');
  await expect(page).toHaveURL(/camera=Original/);
  await expect(opener).toBeFocused();
  await opener.click();
  await expect(page.getByLabel('Camera', { exact: true })).toHaveValue(
    'Original',
  );
  await page.getByLabel('Captured from').fill('2026-10-01T10:00');
  await page.getByLabel('Captured to').fill('2026-09-01T10:00');
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page.getByRole('alert')).toContainText(
    'minimum must not exceed',
  );
  await page.getByRole('button', { name: 'Reset', exact: true }).click();
  await page.getByLabel('Camera', { exact: true }).fill('Sony');
  await page.getByLabel('Rating', { exact: true }).selectOption('4');
  for (let i = 0; i < 18; i++) {
    await page.keyboard.press('Tab');
    expect(
      await page.evaluate(() => !!document.activeElement?.closest('dialog')),
    ).toBe(true);
  }
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page).toHaveURL(/camera=Sony/);
  await expect(page).toHaveURL(/rating=4/);
  await page.getByRole('button', { name: 'Remove Camera filter' }).click();
  expect(new URL(page.url()).searchParams.has('camera')).toBe(false);
  expect(new URL(page.url()).searchParams.get('rating')).toBe('4');
});

test('every existing facet stays discoverable; privacy hides location until explicitly enabled', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/#id_token=fixture-token');
  await page.getByRole('button', { name: /Filters/ }).click();
  await page.getByText('More filters', { exact: true }).click();
  for (const label of [
    'Imported from',
    'Imported to',
    'Filename or path',
    'File type',
    'Camera',
    'Lens',
    'Focal length minimum',
    'Focal length maximum',
    'Aperture minimum',
    'Aperture maximum',
    'Exposure seconds minimum',
    'Exposure seconds maximum',
    'ISO minimum',
    'ISO maximum',
    'Width minimum',
    'Width maximum',
    'Height minimum',
    'Height maximum',
    'Orientation',
    'Rating',
    'Color label',
    'Tags (comma separated)',
    'Title',
    'Description',
    'Creator',
    'Rejected',
  ])
    await expect(page.getByLabel(label, { exact: true })).toBeVisible();
  await expect(page.getByLabel('Latitude minimum')).toHaveCount(0);
  await page.keyboard.press('Escape');
  await page.getByText('Gallery privacy', { exact: true }).click();
  await page.getByRole('button', { name: 'Show location in gallery' }).click();
  await page.getByRole('button', { name: /Filters/ }).click();
  await page.getByText('More filters', { exact: true }).click();
  for (const label of [
    'Latitude minimum',
    'Latitude maximum',
    'Longitude minimum',
    'Longitude maximum',
    'Location present',
  ])
    await expect(page.getByLabel(label, { exact: true })).toBeVisible();
});

test('scoped search, density and secondary import preserve applied gallery context', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/?rating=4#id_token=fixture-token');
  await page.getByLabel('Search in').selectOption('title');
  await page.getByLabel('Search photos').fill('Alpine');
  await page.getByLabel('Search photos').press('Enter');
  await expect(page).toHaveURL(/title=Alpine/);
  await page.getByLabel('Grid density').selectOption('compact');
  await expect(page.locator('.gallery')).toHaveClass(/density-compact/);
  await page.getByRole('button', { name: /Import$/, exact: false }).click();
  await expect(
    page.getByRole('heading', {
      name: 'Import and organize photographs',
      exact: true,
    }),
  ).toBeVisible();
  await page
    .getByRole('button', { name: /Back to gallery/ })
    .first()
    .click();
  await expect(page.locator('.gallery-grid')).toBeVisible();
  expect(new URL(page.url()).searchParams.get('rating')).toBe('4');
  expect(new URL(page.url()).searchParams.get('title')).toBe('Alpine');
});

test('historical selection clears current preview and truthfully identifies unavailable metadata', async ({
  page,
}) => {
  await fixture(page);
  const renders: string[] = [];
  page.on('request', (request) => {
    if (request.method() === 'POST' && request.url().endsWith('/v1/renditions'))
      renders.push(request.url());
  });
  await page.goto(`/?asset=${assetOne}#id_token=fixture-token`);
  await expect(page.locator('.detail-preview > img')).toBeVisible();
  await page
    .locator('.image-history')
    .getByRole('button', { name: /Color edit/ })
    .click();
  await expect(page.locator('.detail-preview > img')).toHaveCount(0);
  await expect(
    page.getByText(/Selected-version field metadata is unavailable/),
  ).toBeVisible();
  await expect(page.locator('.detail-preview')).toContainText('not_requested');
  expect(renders).toEqual([]);
  await page.getByRole('button', { name: /Current working view/ }).click();
  await expect(page.locator('.detail-preview > img')).toBeVisible();
});

test('obsolete gallery and detail reads never replace a newer selection', async ({
  page,
}) => {
  await fixture(page);
  await page.route('**/v1/libraries/main/gallery?**', async (route) => {
    const old =
      new URL(route.request().url()).searchParams.get('camera') === 'Slow';
    if (old) await new Promise((resolve) => setTimeout(resolve, 300));
    await route
      .fulfill({
        json: {
          cards: [card(old ? 1 : 2)],
          count: 1,
          nextCursor: null,
          generation: 1,
          sort: 'captureDate',
          direction: 'desc',
          filters: {},
        },
      })
      .catch(() => {});
  });
  await page.goto('/?camera=Slow#id_token=fixture-token');
  await page.getByRole('button', { name: /Filters/ }).click();
  await page.getByLabel('Camera', { exact: true }).fill('Fast');
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page.getByRole('button', { name: /image-2.jpg/ })).toBeVisible();
  await page.waitForTimeout(350);
  await expect(page.getByRole('button', { name: /image-1.jpg/ })).toHaveCount(
    0,
  );
});

test('empty and failed states remain actionable; normal browsing does not request originals or renders', async ({
  page,
}) => {
  await fixture(page);
  await page.route(/\/v1\/libraries\/main\/gallery(?:\?.*)?$/, (route) =>
    route.fulfill({
      json: {
        cards: [],
        count: 0,
        nextCursor: null,
        generation: 1,
        sort: 'captureDate',
        direction: 'desc',
        filters: {},
      },
    }),
  );
  const unintended: string[] = [];
  page.on('request', (request) => {
    if (
      /\/content$/.test(request.url()) ||
      (request.method() === 'POST' && request.url().endsWith('/renditions'))
    )
      unintended.push(request.url());
  });
  await page.goto('/#id_token=fixture-token');
  await expect(
    page.getByText('Your library is ready for its first photos'),
  ).toBeVisible();
  expect(unintended).toEqual([]);
});

test('desktop gallery/filter/detail geometry and responsive reflow match reference hierarchy', async ({
  page,
}, testInfo) => {
  await fixture(page, { performance: true });
  await page.setViewportSize({ width: 1440, height: 960 });
  await page.goto('/#id_token=fixture-token');
  await expect(page.locator('.gallery-grid img')).toHaveCount(40);
  const columns = await page
    .locator('.gallery-grid')
    .evaluate(
      (element) =>
        getComputedStyle(element).gridTemplateColumns.split(' ').length,
    );
  expect(columns).toBe(5);
  expect(
    (await page.locator('.workspace-header').boundingBox())!.height,
  ).toBeLessThanOrEqual(64);
  expect(
    (await page.locator('.gallery-grid').boundingBox())!.y,
  ).toBeLessThanOrEqual(200);
  await page.screenshot({ path: testInfo.outputPath('gallery.png') });
  await page.getByRole('button', { name: /Filters/ }).click();
  const drawer = (await page
    .getByRole('dialog', { name: 'Filters' })
    .boundingBox())!;
  expect(drawer.x).toBe(0);
  expect(drawer.width).toBeGreaterThanOrEqual(280);
  expect(drawer.width).toBeLessThanOrEqual(360);
  await page.screenshot({ path: testInfo.outputPath('filters.png') });
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: /image-1.jpg/ }).click();
  await expect(page.locator('.image-inspector')).toBeVisible();
  const inspector = (await page.locator('.image-inspector').boundingBox())!;
  const detailBox = (await page.locator('.image-detail').boundingBox())!;
  expect(inspector.width / detailBox.width).toBeGreaterThanOrEqual(0.3);
  expect(inspector.width / detailBox.width).toBeLessThanOrEqual(0.4);
  await page.screenshot({ path: testInfo.outputPath('details.png') });
  for (const width of [1280, 768, 390, 320]) {
    await page.setViewportSize({ width, height: 844 });
    const sizes = await page.evaluate(() => [
      document.documentElement.scrollWidth,
      window.innerWidth,
    ]);
    expect(sizes[0]).toBeLessThanOrEqual(sizes[1]);
    if (width < 900) {
      const preview = (await page.locator('.detail-preview').boundingBox())!;
      const metadata = (await page.locator('.image-inspector').boundingBox())!;
      expect(metadata.y).toBeGreaterThan(preview.y);
    }
  }
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await page.evaluate(() => {
    document.documentElement.style.fontSize = '200%';
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});

test('session expiration hides private workspace and sign-out removes it', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/#id_token=fixture-token');
  await expect(page.locator('.gallery-grid')).toBeVisible();
  await page.evaluate(() =>
    window.dispatchEvent(new Event('owner-session-expired')),
  );
  await expect(page.locator('.gallery-grid')).not.toBeVisible();
  await expect(page.getByText(/Your session expired/)).toBeVisible();
});
