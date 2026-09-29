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
  expect(drawer.width).toBeLessThanOrEqual(360);
  await page.screenshot({ path: testInfo.outputPath('filters.png') });
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
  await page.getByRole('button', { name: 'Web branch main' }).click();
  await expect(page.getByRole('dialog')).toHaveCount(0);
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
