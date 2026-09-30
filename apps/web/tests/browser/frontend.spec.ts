import { expect, test } from '@playwright/test';
import { fixture, assetOne, card, detail, image } from './fixtures';

test('drawer cancels drafts, contains focus, validates ranges, applies and removes chips', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/?camera=Original#id_token=fixture-token');
  const opener = page.getByRole('button', { name: /Filters/ });
  await opener.click();
  await page.getByText('Camera and file', { exact: true }).click();
  await page.getByLabel('Camera', { exact: true }).selectOption('Sony');
  await page.keyboard.press('Escape');
  await expect(page).toHaveURL(/camera=Original/);
  await expect(opener).toBeFocused();
  await opener.click();
  await page.getByText('Camera and file', { exact: true }).click();
  await expect(page.getByLabel('Camera', { exact: true })).toHaveValue(
    'Original',
  );
  await page.getByText('Dates', { exact: true }).click();
  await page.getByLabel('Captured from').fill('2026-10-01T10:00');
  await page.getByLabel('Captured to').fill('2026-09-01T10:00');
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page.getByRole('alert')).toContainText(
    'minimum must not exceed',
  );
  await page.getByRole('button', { name: 'Reset', exact: true }).click();
  await page.getByLabel('Camera', { exact: true }).selectOption('Sony');
  await page.getByText('Organization', { exact: true }).click();
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

test('compact groups use branch values and keep date and numeric ranges usable', async ({
  page,
}) => {
  await fixture(page);
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/#id_token=fixture-token');
  const width = await page
    .locator('.gallery')
    .evaluate((element) => element.getBoundingClientRect().width);
  expect(width).toBeGreaterThanOrEqual(1439);
  await page.getByRole('button', { name: /Filters/ }).click();
  await expect(page.getByLabel('Camera', { exact: true })).toHaveCount(0);
  await page.getByText('Dates', { exact: true }).click();
  const from = (await page.getByLabel('Captured from').boundingBox())!;
  const to = (await page.getByLabel('Captured to').boundingBox())!;
  expect(Math.abs(from.y - to.y)).toBeLessThan(4);
  await page.getByText('Camera and file', { exact: true }).click();
  await page.getByLabel('Camera', { exact: true }).selectOption('Sony');
  await page.getByText('Exposure and size', { exact: true }).click();
  await page.getByLabel('Existing ISO').selectOption('400');
  await expect(page.getByLabel('ISO minimum')).toHaveValue('400');
  await page.getByLabel('ISO maximum').fill('800');
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page).toHaveURL(/camera=Sony/);
  await expect(page).toHaveURL(/isoMin=400/);
  await expect(page).toHaveURL(/isoMax=800/);
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= innerWidth,
    ),
  ).toBe(true);
});

test('long filter choice lists stay searchable', async ({ page }) => {
  await fixture(page);
  await page.route('**/gallery/filter-values?**', (route) => {
    const url = new URL(route.request().url());
    if (url.searchParams.get('facet') !== 'camera') return route.fallback();
    const searched = !!url.searchParams.get('q');
    return route.fulfill({
      json: {
        values: searched
          ? [{ value: 'Rare camera', label: 'Rare camera' }]
          : Array.from({ length: 100 }, (_, index) => ({
              value: `Camera ${index}`,
              label: `Camera ${index}`,
            })),
        hasMore: !searched,
      },
    });
  });
  await page.goto('/#id_token=fixture-token');
  await page.getByRole('button', { name: /Filters/ }).click();
  await page.getByText('Camera and file', { exact: true }).click();
  await expect(page.getByText(/Showing the first 100 values/)).toBeVisible();
  await page.getByLabel('Search Camera choices').fill('Rare');
  await page.getByRole('button', { name: 'Find' }).click();
  await page.getByLabel('Camera', { exact: true }).selectOption('Rare camera');
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page).toHaveURL(/camera=Rare\+camera/);
});

test('every existing facet stays discoverable; privacy hides location until explicitly enabled', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/#id_token=fixture-token');
  await page.getByRole('button', { name: /Filters/ }).click();
  for (const group of [
    'Dates',
    'Camera and file',
    'Exposure and size',
    'Organization',
  ])
    await page.getByText(group, { exact: true }).click();
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
  await page.getByText('Location', { exact: true }).click();
  for (const label of [
    'Latitude minimum',
    'Latitude maximum',
    'Longitude minimum',
    'Longitude maximum',
    'Location present',
  ])
    await expect(page.getByLabel(label, { exact: true })).toBeVisible();
  await page.getByLabel('Existing Latitude').selectOption('40.5');
  await expect(page.getByLabel('Latitude minimum')).toHaveValue('40.5');
  await expect(page.getByLabel('Latitude maximum')).toHaveValue('40.5');
  await page.getByLabel('Existing Longitude').selectOption('-74.25');
  await expect(page.getByLabel('Longitude minimum')).toHaveValue('-74.25');
  await expect(page.getByLabel('Longitude maximum')).toHaveValue('-74.25');
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

test('folder import keeps nested paths and blocks destination collisions before posting', async ({
  page,
}) => {
  await fixture(page);
  const manifests: { items: { path: string }[] }[] = [];
  await page.route('**/v1/import-batches', (route) => {
    manifests.push(route.request().postDataJSON());
    return route.fulfill({
      status: 400,
      json: { code: 'test_stopped_after_manifest' },
    });
  });
  await page.goto('/#id_token=fixture-token');
  await page.getByRole('button', { name: /Import$/ }).click();
  await expect(page.locator('#import-folder')).toHaveAttribute(
    'webkitdirectory',
    '',
  );
  async function select(paths: string[]) {
    await page.evaluate((names) => {
      const transfer = new DataTransfer();
      for (const path of names) {
        const file = new File(['photo'], path.split('/').at(-1)!, {
          type: 'image/jpeg',
        });
        Object.defineProperty(file, 'webkitRelativePath', { value: path });
        transfer.items.add(file);
      }
      const input = document.querySelector<HTMLInputElement>('#import-folder')!;
      input.files = transfer.files;
      input.dispatchEvent(new Event('change', { bubbles: true }));
    }, paths);
  }
  await select(['set/a/photo.jpg', 'set/b/photo.jpg']);
  await page.getByRole('button', { name: 'Import selected files' }).click();
  await expect.poll(() => manifests.length).toBe(1);
  expect(manifests[0].items.map((item) => item.path)).toEqual([
    'set/a/photo.jpg',
    'set/b/photo.jpg',
  ]);
  await select(['set/a/photo.jpg', 'set/A/photo.jpg']);
  await page.getByRole('button', { name: 'Import selected files' }).click();
  await expect(page.getByText(/Duplicate destination path/)).toBeVisible();
  expect(manifests).toHaveLength(1);
});

test('local preview explains that imports cannot be saved', async ({
  page,
}) => {
  await fixture(page, { preview: true });
  await page.goto('/#id_token=fixture-token');
  await page.getByRole('button', { name: /Import$/ }).click();
  await expect(
    page.getByText(/sample gallery preview.*cannot be imported/i),
  ).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Import selected files' }),
  ).toHaveCount(0);
});

test('all sort and search modes use the existing browse query contract', async ({
  page,
}) => {
  await fixture(page);
  const queries: URLSearchParams[] = [];
  page.on('request', (request) => {
    if (new URL(request.url()).pathname === '/v1/libraries/main/gallery')
      queries.push(new URL(request.url()).searchParams);
  });
  await page.goto('/#id_token=fixture-token');
  for (const sort of [
    'captureDate',
    'importDate',
    'filename',
    'rating',
    'dimensions',
  ]) {
    await page.getByLabel('Sort by').selectOption(sort);
    await expect.poll(() => queries.at(-1)?.get('sort')).toBe(sort);
  }
  await page.getByLabel('Direction').selectOption('asc');
  await expect.poll(() => queries.at(-1)?.get('direction')).toBe('asc');
  for (const [scope, field] of [
    ['path', 'path'],
    ['title', 'title'],
    ['description', 'description'],
  ]) {
    await page.getByLabel('Search in').selectOption(scope);
    await page.getByLabel('Search photos').fill('known');
    await page.getByLabel('Search photos').press('Enter');
    await expect.poll(() => queries.at(-1)?.get(field)).toBe('known');
  }
  for (const criteria of [
    '?rating=4&camera=Sony&tag=Landscape',
    '?fileType=jpeg&lens=24-70&rejected=false',
    '?captureFrom=2024-01-01T00%3A00%3A00Z&isoMin=100&widthMin=2000',
  ]) {
    await page.goto(`/${criteria}#id_token=fixture-token`);
    const expected = new URLSearchParams(criteria);
    await expect
      .poll(() => queries.at(-1)?.toString())
      .toContain(expected.toString());
  }
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

test('image overlay retains gallery and compares selected lineage without navigation', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/?rating=4#id_token=fixture-token');
  const tile = page.getByRole('button', { name: /image-1.jpg/ });
  await tile.click();
  await expect(
    page.getByRole('dialog', { name: /Image details/ }),
  ).toBeVisible();
  await expect(page.locator('.gallery-grid')).toBeVisible();
  await page
    .locator('.image-history')
    .getByRole('button', { name: /Color edit/ })
    .click();
  await expect(page.locator('.detail-preview')).toContainText('not_requested');
  await page.locator('.inline-comparison summary').click();
  await expect(
    page.getByRole('heading', { name: 'Version comparison' }),
  ).toBeVisible();
  await expect(
    page.getByRole('heading', { name: 'Committed differences' }),
  ).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: /Image details/ })).toHaveCount(
    0,
  );
  await expect(tile).toBeFocused();
  expect(new URL(page.url()).searchParams.get('rating')).toBe('4');
  await tile.click();
  await page.goBack();
  await expect(page.getByRole('dialog', { name: /Image details/ })).toHaveCount(
    0,
  );
});

test('portrait and panorama previews keep their intrinsic proportions', async ({
  page,
}) => {
  await fixture(page);
  for (const [width, height] of [
    [400, 800],
    [1200, 300],
  ]) {
    await page.goto('about:blank');
    await page.route('**/renditions/**/derivative?**', (route) =>
      route.fulfill({
        json: {
          locator: `data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}"/%3E`,
        },
      }),
    );
    await page.goto(`/?asset=${assetOne}#id_token=fixture-token`);
    const image = page.locator('.detail-preview img');
    await expect(image).toBeVisible();
    await expect
      .poll(() =>
        image.evaluate((element: HTMLImageElement) => ({
          width: element.naturalWidth,
          height: element.naturalHeight,
          fit: getComputedStyle(element).objectFit,
        })),
      )
      .toEqual({ width, height, fit: 'contain' });
    await page.unroute('**/renditions/**/derivative?**');
  }
});

test('merged, hidden and repeated image states disclose ancestry and exact identities', async ({
  page,
}) => {
  await fixture(page);
  await page.route('**/lineage/images/**/history?**', (route) =>
    route.fulfill({
      json: {
        headCommitId: '1'.repeat(64),
        hasHiddenHistory: true,
        nextOffset: 5,
        nodes: [
          {
            commit: {
              commitId: '1'.repeat(64),
              parents: ['2'.repeat(64), '3'.repeat(64)],
              message: 'Repeated edit',
              authoredAt: '2026-09-01T12:00:00Z',
              hidden: false,
            },
            branches: ['main'],
            tags: [],
            hiddenParentIds: ['3'.repeat(64)],
          },
          {
            commit: {
              commitId: '2'.repeat(64),
              parents: [],
              message: 'Repeated edit',
              authoredAt: '2026-08-01T12:00:00Z',
              hidden: false,
            },
            branches: [],
            tags: ['saved'],
            hiddenParentIds: [],
          },
        ],
      },
    }),
  );
  await page.goto(`/?asset=${assetOne}#id_token=fixture-token`);
  const history = page.locator('.image-history');
  await expect(history.locator('.history-list li')).toHaveCount(2);
  await expect(
    history.getByText('Merged or collapsed ancestry; inspect full history.'),
  ).toBeVisible();
  await expect(
    history.getByText(
      'Additional or hidden ancestry is available in full history.',
    ),
  ).toBeVisible();
  await history
    .locator('.history-list li')
    .first()
    .getByText('Version identity')
    .click();
  await expect(
    history.getByText('1'.repeat(64), { exact: true }),
  ).toBeVisible();
  await history
    .locator('.history-list li')
    .nth(1)
    .getByText('Version identity')
    .click();
  await expect(
    history.getByText('2'.repeat(64), { exact: true }),
  ).toBeVisible();
  await expect(history.getByText('saved')).toBeVisible();
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
  await page.getByText('Camera and file', { exact: true }).click();
  await page.getByLabel('Camera', { exact: true }).selectOption('Fast');
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
  console.log(
    `${testInfo.project.name} browser ${page.context().browser()?.version()}`,
  );
  await fixture(page, { performance: true });
  await page.route('**/lineage/images/**/history?**', (route) =>
    route.fulfill({
      json: {
        headCommitId: '1'.repeat(64),
        hasHiddenHistory: false,
        nextOffset: null,
        nodes: ['Color refinement', 'First edit', 'Original import'].map(
          (message, index) => ({
            commit: {
              commitId: String(index + 1).repeat(64),
              parents: index < 2 ? [String(index + 2).repeat(64)] : [],
              message,
              authoredAt: '2026-09-01T12:00:00Z',
              hidden: false,
            },
            branches: index === 0 ? ['main'] : [],
            tags: [],
            hiddenParentIds: [],
          }),
        ),
      },
    }),
  );
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
  expect(drawer.width).toBeLessThanOrEqual(460);
  await page.screenshot({ path: testInfo.outputPath('filters.png') });
  await page.getByText('Dates', { exact: true }).click();
  await page.screenshot({ path: testInfo.outputPath('filters-dates.png') });
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: /image-1.jpg/ }).click();
  await expect(page.locator('.image-inspector')).toBeVisible();
  await expect(page.getByText('Sony A7 IV')).toBeVisible();
  await expect(
    page.getByRole('heading', { name: 'Version history', exact: true }),
  ).toBeVisible();
  const secondVersion = (await page
    .locator('.image-history .history-list li')
    .nth(1)
    .boundingBox())!;
  expect(secondVersion.y + secondVersion.height).toBeLessThanOrEqual(960);
  const inspector = (await page.locator('.image-inspector').boundingBox())!;
  const detailBox = (await page.locator('.image-detail').boundingBox())!;
  expect(inspector.width / detailBox.width).toBeGreaterThanOrEqual(0.3);
  expect(inspector.width / detailBox.width).toBeLessThanOrEqual(0.4);
  await page.screenshot({ path: testInfo.outputPath('details.png') });
  await page.setViewportSize({ width: 1280, height: 800 });
  const secondAt1280 = (await page
    .locator('.image-history .history-list li')
    .nth(1)
    .boundingBox())!;
  expect(secondAt1280.y + secondAt1280.height).toBeLessThanOrEqual(800);
  await page.screenshot({ path: testInfo.outputPath('details-1280.png') });
  await page.getByRole('button', { name: /Back to results/ }).click();
  await page.screenshot({ path: testInfo.outputPath('gallery-1280.png') });
  await page.getByRole('button', { name: /Filters/ }).click();
  await page.screenshot({ path: testInfo.outputPath('filters-1280.png') });
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: /image-1.jpg/ }).click();
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
    const sizes = Array.from(
      document.querySelectorAll<HTMLElement>('body *'),
    ).map(
      (element) =>
        [element, parseFloat(getComputedStyle(element).fontSize)] as const,
    );
    for (const [element, size] of sizes)
      element.style.fontSize = `${size * 2}px`;
  });
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});

test('session expiration hides private workspace', async ({ page }) => {
  await fixture(page);
  await page.goto('/#id_token=fixture-token');
  await expect(page.locator('.gallery-grid')).toBeVisible();
  await page.evaluate(() =>
    window.dispatchEvent(new Event('owner-session-expired')),
  );
  await expect(page.locator('.gallery-grid')).not.toBeVisible();
  await expect(page.getByText(/Your session expired/)).toBeVisible();
});

test('unsaved drafts block branch changes, survive reauthentication and are cleared by sign-out', async ({
  page,
}) => {
  await fixture(page);
  await page.goto(`/?asset=${assetOne}&branch=main#id_token=fixture-token`);
  await page.locator('.metadata-edit > summary').click();
  await page.getByLabel('Field', { exact: true }).selectOption('title');
  await page.getByLabel('Value', { exact: true }).fill('Retained draft');
  page.once('dialog', (dialog) => dialog.dismiss());
  await page.getByRole('button', { name: 'Switch web branch' }).click();
  await expect(
    page.getByRole('dialog', { name: /Switch web branch/ }),
  ).toHaveCount(0);
  page.once('dialog', (dialog) => dialog.dismiss());
  await page.getByRole('button', { name: /Back to results/ }).click();
  await expect(page.getByLabel('Value', { exact: true })).toHaveValue(
    'Retained draft',
  );
  // The reauthentication round-trip reloads the page with a fresh owner token.
  page.once('dialog', (dialog) => dialog.accept());
  await page.goto(
    `/?asset=${assetOne}&branch=main#id_token=fresh-fixture-token`,
  );
  await page.locator('.metadata-edit > summary').click();
  await expect(page.getByLabel('Value', { exact: true })).toHaveValue(
    'Retained draft',
  );
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: /Back to results/ }).click();
  await page.getByLabel('Owner controls').click();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await page.waitForURL(/\/logout/);
  await expect(page.locator('.gallery')).toHaveCount(0);
  expect(
    await page.evaluate(() =>
      Object.keys(sessionStorage).some((key) =>
        key.startsWith('gallery.draft.'),
      ),
    ),
  ).toBe(false);
});

test('returning from detail restores scroll and initiating tile focus', async ({
  page,
}) => {
  await fixture(page, { performance: true });
  await page.goto('/?camera=Fixture&sort=rating#id_token=fixture-token');
  const tile = page.getByRole('button', { name: /image-30.jpg/ });
  await tile.scrollIntoViewIfNeeded();
  const scroll = await page.evaluate(() => window.scrollY);
  await tile.click();
  await expect(page.locator('.image-inspector')).toBeVisible();
  await page.getByRole('button', { name: /Back to results/ }).click();
  await expect(tile).toBeFocused();
  expect(
    Math.abs((await page.evaluate(() => window.scrollY)) - scroll),
  ).toBeLessThan(5);
  expect(new URL(page.url()).searchParams.get('camera')).toBe('Fixture');
});

test('returning from Import restores a scrolled gallery', async ({ page }) => {
  await fixture(page, { performance: true });
  await page.goto('/?camera=Fixture#id_token=fixture-token');
  const tile = page.getByRole('button', { name: /image-30.jpg/ });
  await tile.scrollIntoViewIfNeeded();
  const scroll = await page.evaluate(() => window.scrollY);
  expect(scroll).toBeGreaterThan(0);
  await page.getByRole('button', { name: /Import$/ }).click();
  await expect(
    page.getByRole('heading', { name: 'Import and organize photographs' }),
  ).toBeVisible();
  await page
    .getByRole('button', { name: /Back to gallery/ })
    .first()
    .click();
  await expect(tile).toBeVisible();
  expect(
    Math.abs((await page.evaluate(() => window.scrollY)) - scroll),
  ).toBeLessThan(5);
  expect(new URL(page.url()).searchParams.get('camera')).toBe('Fixture');
});

test('library history uses library-wide records and opens image lineage', async ({
  page,
}) => {
  await fixture(page);
  await page.route('**/version-control/graph/**', (route) =>
    route.fulfill({
      json: [
        {
          commitId: '1'.repeat(64),
          parents: [],
          message: 'Library-wide import',
          authoredAt: '2026-09-01T12:00:00Z',
          hidden: false,
          changes: [{ assetId: assetOne, path: 'photos/image-1.jpg' }],
        },
      ],
    }),
  );
  await page.goto('/#id_token=fixture-token');
  await page.getByRole('button', { name: 'History', exact: true }).click();
  await expect(
    page.getByRole('heading', { name: 'Library history', exact: true }),
  ).toBeVisible();
  await expect(page.getByText('Library-wide import')).toBeVisible();
  await page
    .getByRole('button', { name: 'History for photos/image-1.jpg' })
    .click();
  await expect(
    page.getByRole('heading', { name: 'Image lineage' }),
  ).toBeVisible();
});

test('historical preview never displays mismatched version provenance', async ({
  page,
}) => {
  await fixture(page);
  await page.route('**/lineage/images/**/compare?**', (route) =>
    route.fulfill({
      json: {
        targetCommitId: '1'.repeat(64),
        sourceCommitId: '1'.repeat(64),
        baseCommitId: null,
        targetStates: [
          {
            commitId: '1'.repeat(64),
            assetId: assetOne,
            editId: 'edit',
            xmpSha256: 'a'.repeat(64),
            path: 'image.jpg',
            lifecycle: 'active',
            previewState: 'completed',
            previewRequestId: 'wrong-preview',
            previewProvenance: {
              selectedVersion: '1'.repeat(64),
              xmpSha256: 'a'.repeat(64),
              sourceSha256: 'b'.repeat(64),
              rendererRelease: 'fixture',
              rendererArtifactDigest: 'sha256:fixture',
              profile: 'fixture',
              compatibilityMatrix: 'fixture',
              completedAtUtc: '2026-09-01T12:00:00Z',
            },
          },
        ],
        sourceStates: [],
        differences: [],
      },
    }),
  );
  await page.goto(`/?asset=${assetOne}#id_token=fixture-token`);
  await page
    .locator('.image-history')
    .getByRole('button', { name: /Color edit/ })
    .click();
  await expect(page.locator('.detail-preview')).toContainText('unavailable');
  await expect(page.locator('.detail-preview img')).toHaveCount(0);
});

test('expanded control labels and design tokens remain readable', async ({
  page,
}, testInfo) => {
  await fixture(page, { performance: true });
  await page.goto('/#id_token=fixture-token');
  await expect(page.locator('.gallery-grid img')).toHaveCount(40);
  const contrast = await page.evaluate(() => {
    const style = getComputedStyle(document.documentElement);
    const rgb = (hex: string) =>
      hex.match(/[a-f\d]{2}/gi)!.map((channel) => parseInt(channel, 16) / 255);
    const lightness = (hex: string) =>
      rgb(hex)
        .map((value) =>
          value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4,
        )
        .reduce(
          (sum, value, index) => sum + value * [0.2126, 0.7152, 0.0722][index],
          0,
        );
    const ratio = (a: string, b: string) => {
      const values = [lightness(a), lightness(b)].sort((x, y) => y - x);
      return (values[0] + 0.05) / (values[1] + 0.05);
    };
    return {
      text: ratio('#242621', '#f7f6f2'),
      muted: ratio(style.getPropertyValue('--muted').trim(), '#f7f6f2'),
      control: ratio(
        style.getPropertyValue('--control-line').trim(),
        '#fafaf7',
      ),
    };
  });
  expect(contrast.text).toBeGreaterThanOrEqual(4.5);
  expect(contrast.muted).toBeGreaterThanOrEqual(4.5);
  expect(contrast.control).toBeGreaterThanOrEqual(3);
  await page.evaluate(() => {
    for (const element of document.querySelectorAll<HTMLElement>(
      '.workspace-header button, .gallery-toolbar button',
    )) {
      const label = element.textContent?.trim() ?? '';
      element.textContent = `${label} ${label.slice(0, Math.ceil(label.length * 0.3))}`;
    }
    for (const option of document.querySelectorAll<HTMLOptionElement>(
      '.gallery-toolbar option',
    )) {
      option.textContent += option.textContent.slice(
        0,
        Math.ceil(option.textContent.length * 0.3),
      );
    }
  });
  for (const width of [1440, 320]) {
    await page.setViewportSize({ width, height: 844 });
    expect(
      await page.evaluate(
        () => document.documentElement.scrollWidth <= innerWidth,
      ),
    ).toBe(true);
    const controlsFit = await page.evaluate(() =>
      Array.from(
        document.querySelectorAll<HTMLElement>(
          '.workspace-header button, .gallery-toolbar button, .gallery-toolbar select',
        ),
      ).every((element) => {
        const bounds = element.getBoundingClientRect();
        return bounds.left >= 0 && bounds.right <= innerWidth;
      }),
    );
    expect(controlsFit).toBe(true);
    await page.screenshot({
      path: testInfo.outputPath(`expanded-${width}.png`),
    });
  }
});

test('selected version is retained through full lineage and back to detail', async ({
  page,
}) => {
  await fixture(page);
  await page.goto(`/?asset=${assetOne}&camera=Sony#id_token=fixture-token`);
  await page
    .locator('.image-history')
    .getByRole('button', { name: /Color edit/ })
    .click();
  await expect(page).toHaveURL(/imageVersion=1{64}/);
  await page.getByRole('button', { name: 'View image history' }).click();
  await expect(page.getByLabel('Target version')).toHaveValue('1'.repeat(64));
  await page.getByRole('button', { name: 'Back to image details' }).click();
  await expect(page.locator('.detail-preview')).toContainText(
    'Selected version commit',
  );
  await expect(
    page.locator('.image-history').getByRole('button', { name: /Color edit/ }),
  ).toHaveAttribute('aria-pressed', 'true');
  expect(new URL(page.url()).searchParams.get('camera')).toBe('Sony');
});

test('backdrop cancellation, responsive gallery and drawer timing remain usable', async ({
  page,
}) => {
  await fixture(page, { performance: true });
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto('/#id_token=fixture-token');
  await expect(page.locator('.gallery-grid img')).toHaveCount(40);
  expect(
    await page
      .locator('.gallery-grid')
      .evaluate(
        (element) =>
          getComputedStyle(element).gridTemplateColumns.split(' ').length,
      ),
  ).toBe(4);
  const samples: number[] = [];
  for (let i = 0; i < 20; i++) {
    const start = Date.now();
    await page.getByRole('button', { name: /Filters/ }).click();
    await expect(page.getByRole('dialog', { name: 'Filters' })).toBeVisible();
    samples.push(Date.now() - start);
    await page.mouse.click(900, 400);
    await expect(page.getByRole('dialog')).toHaveCount(0);
  }
  samples.sort((a, b) => a - b);
  console.log(`Drawer usable p95=${samples[18]} ms`);
  expect(samples[18]).toBeLessThanOrEqual(200);
  await page.setViewportSize({ width: 320, height: 844 });
  await expect(page.getByRole('button', { name: /Filters/ })).toBeVisible();
  expect(
    await page.evaluate(
      () => document.documentElement.scrollWidth <= window.innerWidth,
    ),
  ).toBe(true);
});
