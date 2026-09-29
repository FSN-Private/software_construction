import { expect, test } from '@playwright/test';
import { fixture, card, assetOne, browserTestOrigin, image } from './fixtures';

test('owner browse, paging context, privacy, details, and edit conflict', async ({
  page,
}) => {
  await fixture(page);
  await page.goto('/#id_token=fixture-token');
  await expect(page.getByRole('button', { name: /image-1.jpg/ })).toBeVisible();
  await expect(page.getByLabel('Latitude minimum')).toHaveCount(0);
  await page.getByText('Gallery privacy', { exact: true }).click();
  await page.getByRole('button', { name: 'Show location in gallery' }).click();
  await page.getByRole('button', { name: /Filters/ }).click();
  await page.getByText('More filters', { exact: true }).click();
  await expect(page.getByLabel('Latitude minimum')).toBeVisible();
  await page.getByLabel('Camera', { exact: true }).fill('Fixture camera');
  await page.getByRole('button', { name: 'Show photos' }).click();
  await expect(page).toHaveURL(/camera=Fixture\+camera/);
  await page.getByRole('button', { name: 'Next page' }).click();
  await expect(page.getByRole('button', { name: /image-2.jpg/ })).toBeVisible();
  await page.getByRole('button', { name: /image-2.jpg/ }).click();
  await expect(
    page.getByRole('heading', { name: 'Fixture title' }),
  ).toBeVisible();
  await expect(page.getByText(/No web working change/)).toBeVisible();
  await page.evaluate(() => {
    window.open = (url) => {
      document.body.dataset.previewLocator = String(url);
      return null;
    };
  });
  await page.getByText('Image actions', { exact: true }).click();
  await page.getByRole('button', { name: 'Open full preview' }).click();
  await expect(page.locator('body')).toHaveAttribute(
    'data-preview-locator',
    image,
  );
  await page.locator('.metadata-edit > summary').click();
  await page.getByRole('button', { name: 'Save working change' }).click();
  await expect(page.getByText(/working state changed/)).toBeVisible();
  await page.getByRole('button', { name: 'Save working change' }).click();
  await expect(page.getByText(/uncommitted working change/)).toBeVisible();
  await page.getByRole('button', { name: /Back to results/ }).click();
  await expect(page.getByRole('button', { name: /image-2.jpg/ })).toBeVisible();
  await expect(page).toHaveURL(/cursor=page-two/);
});

test('gallery history retains browse context and exposes graph and table', async ({
  page,
}) => {
  await fixture(page);
  const original = `/?rating=4&sort=rating&direction=asc&cursor=page-two&branch=main&asset=${assetOne}`;
  await page.goto(`${original}#id_token=fixture-token`);
  await expect(
    page.getByRole('button', { name: 'View image history' }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'View image history' }).click();
  await expect(
    page.getByRole('heading', { name: 'Image lineage' }),
  ).toBeVisible();
  await expect(
    page.getByRole('list', { name: 'Image lineage graph' }),
  ).toBeVisible();
  await expect(
    page.getByRole('table', {
      name: 'Exact commits and parent relationships for this image',
    }),
  ).toBeVisible();
  await expect(
    page.getByText(/Hidden image history exists on this branch/),
  ).toBeVisible();
  await page.getByRole('checkbox', { name: 'Show hidden commits' }).check();
  await expect(
    page.getByRole('button', { name: /Intermediate edit/ }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Back to image details' }).click();
  await expect(
    page.getByRole('button', { name: 'View image history' }),
  ).toBeVisible();
  for (const [key, value] of new URL(original, browserTestOrigin).searchParams)
    expect(new URL(page.url()).searchParams.get(key)).toBe(value);
  expect(new URL(page.url()).searchParams.has('history')).toBe(false);
});

test('exact comparison keeps textual differences when a preview fails', async ({
  page,
}) => {
  await fixture(page);
  await page.goto(`/?branch=main&asset=${assetOne}#id_token=fixture-token`);
  await page.getByRole('button', { name: 'View image history' }).click();
  await page.getByRole('checkbox', { name: 'Show hidden commits' }).check();
  await page.getByRole('button', { name: 'Compare versions' }).click();
  await expect(
    page.getByRole('heading', { name: 'Version comparison' }),
  ).toBeVisible();
  await expect(
    page.getByRole('table', {
      name: 'Exact image differences between selected commits',
    }),
  ).toBeVisible();
  await expect(page.getByText(/common ancestor/)).toContainText('none');
  await expect(
    page.getByRole('region', {
      name: 'XMP fields for 30000000-0000-0000-0000-000000000001',
    }),
  ).toContainText('Source title');
  const targetPreview = page.getByRole('region', { name: 'Target preview' });
  await targetPreview
    .getByRole('button', { name: 'Request exact preview' })
    .click();
  await expect(targetPreview.getByRole('status')).toContainText('pending');
  await targetPreview
    .getByRole('button', { name: 'Refresh preview status' })
    .click();
  await expect(targetPreview.getByRole('status')).toContainText('failed');
  await expect(
    page.getByRole('table', {
      name: 'Exact image differences between selected commits',
    }),
  ).toBeVisible();
});

test('500-version lineage keeps the first page bounded and usable', async ({
  page,
}) => {
  test.setTimeout(120_000);
  await fixture(page);
  const id = (index: number) => index.toString(16).padStart(64, '0');
  await page.route(
    (url) =>
      url.pathname.includes('/lineage/images/') &&
      url.pathname.endsWith('/history'),
    async (route) => {
      const url = new URL(route.request().url());
      const offset = Number(url.searchParams.get('offset') ?? 0);
      const nodes = Array.from(
        { length: Math.min(100, 500 - offset) },
        (_, position) => {
          const index = 499 - offset - position;
          return {
            commit: {
              commitId: id(index),
              parents: index ? [id(index - 1)] : [],
              authorId: 'owner',
              authoredAt: new Date(
                Date.UTC(2026, 8, 26, 0, 0, index),
              ).toISOString(),
              message: `Version ${index}`,
              hidden: false,
            },
            branches: index === 499 ? ['main'] : [],
            tags: [],
            hiddenParentIds: [],
          };
        },
      );
      await route.fulfill({
        json: {
          assetId: assetOne,
          branch: 'main',
          headCommitId: id(499),
          offset,
          nextOffset: offset + 100 < 500 ? offset + 100 : null,
          nodes,
        },
      });
    },
  );
  const samples: number[] = [];
  for (let run = 0; run < 25; run++) {
    const started = Date.now();
    await page.goto(`/?branch=main&asset=${assetOne}#id_token=fixture-token`);
    await page.getByRole('button', { name: 'View image history' }).click();
    await expect(
      page
        .getByRole('table', {
          name: 'Exact commits and parent relationships for this image',
        })
        .locator('tbody tr'),
    ).toHaveCount(100);
    if (run >= 5) samples.push(Date.now() - started);
  }
  samples.sort((a, b) => a - b);
  const p95 = samples[Math.ceil(samples.length * 0.95) - 1];
  console.log(
    `${test.info().project.name}: 500-version lineage usable p95=${p95} ms; samples=${samples.join(',')}`,
  );
  expect(p95).toBeLessThan(2000);
  await page.getByRole('button', { name: 'Next history page' }).click();
  await expect(
    page
      .getByRole('table', {
        name: 'Exact commits and parent relationships for this image',
      })
      .locator('tbody tr'),
  ).toHaveCount(100);
  await expect(
    page
      .getByRole('table', {
        name: 'Exact commits and parent relationships for this image',
      })
      .getByText('Version 399', { exact: true }),
  ).toBeVisible();
});

test('lineage dates follow three locale and time-zone settings', async ({
  browser,
}) => {
  test.setTimeout(60_000);
  for (const [locale, timezoneId] of [
    ['en-US', 'America/New_York'],
    ['fr-FR', 'Europe/Paris'],
    ['th-TH', 'Asia/Bangkok'],
  ] as const) {
    const context = await browser.newContext({
      locale,
      timezoneId,
      baseURL: browserTestOrigin,
    });
    try {
      const page = await context.newPage();
      await fixture(page);
      await page.goto(`/?branch=main&asset=${assetOne}#id_token=fixture-token`);
      await page.getByRole('button', { name: 'View image history' }).click();
      const expected = await page.evaluate(() =>
        new Intl.DateTimeFormat(undefined, {
          dateStyle: 'medium',
          timeStyle: 'short',
        }).format(new Date('2026-09-26T00:00:00Z')),
      );
      expect(
        await page.evaluate(
          () => Intl.DateTimeFormat().resolvedOptions().timeZone,
        ),
      ).toBe(timezoneId);
      await expect(
        page
          .getByRole('table', {
            name: 'Exact commits and parent relationships for this image',
          })
          .getByRole('row')
          .filter({ hasText: 'Color edit' }),
      ).toContainText(expected);
      await expect(
        page.getByText('Image lineage', { exact: true }),
      ).toBeVisible();
    } finally {
      await context.close();
    }
  }
});

test('mutation pause keeps lineage readable and disables history controls', async ({
  page,
}) => {
  await fixture(page);
  await page.route(
    (url) => url.pathname.endsWith('/lineage/capabilities'),
    (route) => route.fulfill({ json: { mutationsEnabled: false } }),
  );
  await page.goto(`/?branch=main&asset=${assetOne}#id_token=fixture-token`);
  await page.getByRole('button', { name: 'View image history' }).click();
  await expect(
    page.getByRole('table', {
      name: 'Exact commits and parent relationships for this image',
    }),
  ).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Hide selected version' }),
  ).toBeDisabled();
  await expect(
    page.getByRole('button', { name: 'Switch branch' }),
  ).toBeDisabled();
  await page.getByLabel('Source branch').selectOption('source');
  await page.getByRole('button', { name: 'Prepare merge' }).click();
  await expect(
    page.getByRole('button', { name: 'Review exact merge result' }),
  ).toBeDisabled();
});

test('merge review requires conflict choice and exact-result confirmation', async ({
  page,
}) => {
  await fixture(page);
  await page.goto(`/?branch=main&asset=${assetOne}#id_token=fixture-token`);
  await page.getByRole('button', { name: 'View image history' }).click();
  await page.getByLabel('Source branch').selectOption('source');
  await page.getByRole('button', { name: 'Prepare merge' }).click();
  await expect(page.getByText('target title')).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Review exact merge result' }),
  ).toBeDisabled();
  await page
    .getByLabel(`Resolve title for image ${assetOne}`)
    .selectOption('source');
  await page.getByRole('button', { name: 'Review exact merge result' }).click();
  await expect(
    page.getByRole('region', { name: 'Exact reviewed merge result' }),
  ).toBeVisible();
  await page.getByText('Exact proposed XMP bytes').click();
  await expect(page.getByText('<x:xmpmeta>reviewed</x:xmpmeta>')).toBeVisible();
  await page.getByLabel('Merge commit message').fill('Merge reviewed edits');
  await page
    .getByRole('checkbox', {
      name: /I reviewed the complete proposed library result/,
    })
    .check();
  const confirm = page.waitForRequest((request) =>
    request.url().endsWith('/lineage/merges/confirm'),
  );
  await page.getByRole('button', { name: 'Create merge commit' }).click();
  const sent = await confirm;
  expect(sent.postDataJSON().reviewDigest).toBe('5'.repeat(64));
  expect(
    sent.postDataJSON().reviewed.resolutions[
      `${assetOne}/30000000-0000-0000-0000-000000000001:title`
    ],
  ).toBe('source');
});

test('branch switch blocks dirty gallery state until it is stashed', async ({
  page,
}) => {
  await fixture(page);
  await page.goto(
    `/?rating=4&branch=main&asset=${assetOne}#id_token=fixture-token`,
  );
  await page.getByRole('button', { name: 'View image history' }).click();
  await expect(page.getByText(/1 gallery web changes/)).toBeVisible();
  await page.getByLabel('Switch to branch').selectOption('source');
  await page.getByRole('button', { name: 'Switch branch' }).click();
  await expect(page.getByText(/dirty_checkout/)).toBeVisible();
  expect(new URL(page.url()).searchParams.get('branch')).toBe('main');
  await page.getByRole('radio', { name: 'Preserve in a stash' }).check();
  const checkout = page.waitForRequest(
    (request) =>
      request.url().endsWith('/version-control/checkout') &&
      request.method() === 'POST' &&
      request.postDataJSON().dirtyAction === 'preserve',
  );
  await page.getByRole('button', { name: 'Switch branch' }).click();
  const sent = await checkout;
  expect(sent.headers()['if-match']).toBe('0');
  await expect(page).toHaveURL(/branch=source/);
  expect(new URL(page.url()).searchParams.get('rating')).toBe('4');
  expect(new URL(page.url()).searchParams.get('asset')).toBe(assetOne);
  await expect(
    page.getByRole('button', { name: 'Restore this stash' }),
  ).toBeVisible();
  const restoreStash = page.waitForRequest((request) =>
    request
      .url()
      .endsWith(
        '/version-control/stashes/90000000-0000-0000-0000-000000000001/restore',
      ),
  );
  await page.getByRole('button', { name: 'Restore this stash' }).click();
  expect((await restoreStash).headers()['x-client-id']).toBe('__lineage_web__');
  await expect(page.getByText(/1 gallery web changes/)).toBeVisible();
});

test('history actions bind selected commits, staged gallery edits, and confirmation', async ({
  page,
}) => {
  test.setTimeout(60_000);
  await fixture(page);
  await page.goto(`/?branch=main&asset=${assetOne}#id_token=fixture-token`);
  await page.getByRole('button', { name: 'View image history' }).click();
  await page.getByLabel('New branch or tag name').fill('reviewed-tag');
  const tag = page.waitForRequest((request) =>
    request.url().endsWith('/version-control/refs/tag'),
  );
  await page
    .getByRole('button', { name: 'Create tag at selected version' })
    .click();
  expect((await tag).postDataJSON().commitId).toBe('1'.repeat(64));
  await expect(
    page.getByRole('button', { name: 'Hide selected version' }),
  ).toBeEnabled();
  await page.getByLabel('Remove tag').selectOption('reviewed-tag');
  await page
    .getByRole('checkbox', { name: 'Confirm reference removal' })
    .check();
  await expect(
    page.getByRole('button', { name: 'Remove selected tag' }),
  ).toBeEnabled();
  const deleted = page.waitForRequest(
    (request) =>
      request.method() === 'DELETE' &&
      request.url().endsWith('/version-control/refs/reviewed-tag'),
  );
  await page.getByRole('button', { name: 'Remove selected tag' }).click();
  expect((await deleted).headers()['if-match']).toBe('1');
  const visibility = page.waitForRequest((request) =>
    request.url().endsWith('/version-control/visibility'),
  );
  await page.getByRole('button', { name: 'Hide selected version' }).click();
  expect((await visibility).postDataJSON()).toMatchObject({
    commitId: '1'.repeat(64),
    hidden: true,
  });
  await page.getByRole('button', { name: 'Back to image details' }).click();
  await page.locator('.metadata-edit > summary').click();
  await page.getByRole('button', { name: 'Save working change' }).click();
  await page.getByRole('button', { name: 'Save working change' }).click();
  await page.getByRole('button', { name: 'View image history' }).click();
  await page
    .getByRole('checkbox', { name: /Image .* working version 1/ })
    .check();
  await page
    .getByLabel('Commit or restore message')
    .fill('Commit gallery edit');
  await page.getByRole('button', { name: 'Prepare selected commit' }).click();
  await expect(page.getByText(/No commit exists yet/)).toBeVisible();
  const commit = page.waitForRequest(
    (request) =>
      request.url().endsWith('/version-control/commits') &&
      request.method() === 'POST',
  );
  await page.getByRole('button', { name: 'Confirm and create commit' }).click();
  expect((await commit).postDataJSON()).toMatchObject({
    expectedHead: '1'.repeat(64),
    message: 'Commit gallery edit',
  });
  await expect(
    page.getByText('No gallery working changes are ready to stage.'),
  ).toBeVisible();
  await page
    .getByLabel('Commit or restore message')
    .fill('Restore selected state');
  await page
    .getByRole('checkbox', { name: 'Confirm a new version-control commit' })
    .check();
  await expect(
    page.getByRole('button', { name: 'Create restore commit' }),
  ).toBeEnabled();
  const restore = page.waitForRequest((request) =>
    request.url().endsWith('/version-control/restore'),
  );
  await page.getByRole('button', { name: 'Create restore commit' }).click();
  expect((await restore).postDataJSON()).toMatchObject({
    branch: 'main',
    targetCommitId: '1'.repeat(64),
    expectedHead: '1'.repeat(64),
  });
});

test('non-default rewrite previews displaced history and recovery names a branch', async ({
  page,
}) => {
  await fixture(page);
  await page.goto(`/?branch=source&asset=${assetOne}#id_token=fixture-token`);
  await page.getByRole('button', { name: 'View image history' }).click();
  await page
    .getByRole('button', { name: 'Preview branch rewrite to selected version' })
    .click();
  await expect(page.getByText(/1 commits leave this branch/)).toBeVisible();
  await page
    .getByRole('checkbox', { name: 'I reviewed every displaced commit' })
    .check();
  const rewrite = page.waitForRequest(
    (request) =>
      request.url().endsWith('/version-control/rewrite') &&
      request.method() === 'POST',
  );
  const refreshedHistory = page.waitForResponse(
    (response) =>
      response.url().includes('/lineage/images/') &&
      new URL(response.url()).pathname.endsWith('/history'),
  );
  await page.getByRole('button', { name: 'Confirm branch rewrite' }).click();
  expect((await rewrite).postDataJSON()).toMatchObject({
    branch: 'source',
    previewToken: 'fixture-preview',
    confirm: true,
  });
  await refreshedHistory;
  await expect(page.getByLabel('Eligible reflog entry')).toBeVisible();
  await page
    .getByLabel('Eligible reflog entry')
    .selectOption('90000000-0000-0000-0000-000000000003');
  await page.getByLabel('New recovery branch name').fill('recovered-source');
  const recovery = page.waitForRequest((request) =>
    request
      .url()
      .endsWith(
        '/version-control/recovery/90000000-0000-0000-0000-000000000003',
      ),
  );
  await page.getByRole('button', { name: 'Create recovery branch' }).click();
  expect((await recovery).postDataJSON()).toMatchObject({
    name: 'recovered-source',
  });
});

test('stale authentication sends rewrite through hosted logout and fresh sign-in', async ({
  page,
}) => {
  await fixture(page);
  await page.route(
    (url) => url.pathname.endsWith('/version-control/rewrite'),
    (route) =>
      route.fulfill({
        status: 403,
        json: { code: 'recent_authentication_required' },
      }),
  );
  await page.route('**/logout?*', (route) =>
    route.fulfill({
      contentType: 'text/html',
      body: '<title>Hosted sign-out</title>',
    }),
  );
  await page.goto(`/?branch=source&asset=${assetOne}#id_token=fixture-token`);
  await page.getByRole('button', { name: 'View image history' }).click();
  await page
    .getByRole('button', { name: 'Preview branch rewrite to selected version' })
    .click();
  await page
    .getByRole('checkbox', { name: 'I reviewed every displaced commit' })
    .check();
  await page.getByRole('button', { name: 'Confirm branch rewrite' }).click();
  await expect(
    page.getByRole('button', { name: 'Sign in again for recovery or rewrite' }),
  ).toBeVisible();
  await page
    .getByRole('button', { name: 'Sign in again for recovery or rewrite' })
    .click();
  await expect(page).toHaveURL(/\/logout\?/);
  const redirect = new URL(page.url());
  expect(redirect.searchParams.get('response_type')).toBe('token');
  expect(redirect.searchParams.get('state')).toMatch(/^[0-9a-f-]{36}$/);
  expect(
    await page.evaluate(
      (state) => sessionStorage.getItem(`lineage.reauth.${state}`),
      redirect.searchParams.get('state'),
    ),
  ).toContain('branch=source');
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
      body: `window.__PLATFORM_CONFIG__={apiBaseUrl:"${browserTestOrigin}",cognitoHostedUiUrl:"${browserTestOrigin}",cognitoClientId:"fixture",redirectUri:"${browserTestOrigin}",galleryLibraryId:"capacity"};`,
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
