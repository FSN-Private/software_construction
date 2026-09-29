import { expect, test, type Page } from '@playwright/test';

const browserTestOrigin = `http://127.0.0.1:${process.env.BROWSER_TEST_PORT ?? 4173}`;

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
  let checkoutVersion = 0;
  let galleryDirty = 1;
  const stashes: { id: string; sourceRef: string; createdAt: string }[] = [];
  const refs = [
    {
      name: 'main',
      kind: 'branch',
      commitId: '1'.repeat(64),
      protected: true,
      version: 2,
    },
    {
      name: 'source',
      kind: 'branch',
      commitId: '3'.repeat(64),
      protected: false,
      version: 1,
    },
  ];
  await page.route('**/platform-config.js', (route) =>
    route.fulfill({
      contentType: 'text/javascript',
      body: `window.__PLATFORM_CONFIG__={apiBaseUrl:"${browserTestOrigin}",cognitoHostedUiUrl:"${browserTestOrigin}",cognitoClientId:"fixture",redirectUri:"${browserTestOrigin}",galleryLibraryId:"main"};`,
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
    if (path.endsWith('/lineage/capabilities'))
      return route.fulfill({ json: { mutationsEnabled: true } });
    if (path === '/v1/assets') return route.fulfill({ json: [] });
    if (path.endsWith('/version-control/checkout')) {
      if (request.method() === 'GET')
        return route.fulfill({
          json: {
            branch: checkoutVersion ? 'source' : null,
            version: checkoutVersion,
            browserWorkingCount: 0,
            galleryWorkingCount: galleryDirty,
            deviceConflictCount: 0,
          },
        });
      const body = request.postDataJSON();
      if (request.headers()['if-match'] !== String(checkoutVersion))
        return route.fulfill({
          status: 409,
          json: { code: 'checkout_conflict' },
        });
      if (galleryDirty && body.dirtyAction === 'block')
        return route.fulfill({ status: 409, json: { code: 'dirty_checkout' } });
      if (
        galleryDirty &&
        body.dirtyAction === 'discard' &&
        !body.confirmDiscard
      )
        return route.fulfill({
          status: 409,
          json: { code: 'confirmation_required' },
        });
      if (galleryDirty && body.dirtyAction === 'preserve')
        stashes.push({
          id: '90000000-0000-0000-0000-000000000001',
          sourceRef: 'main',
          createdAt: '2026-09-26T00:00:00Z',
        });
      checkoutVersion++;
      galleryDirty = 0;
      return route.fulfill({
        json: {
          refName: body.branch,
          version: checkoutVersion,
          stashId:
            body.dirtyAction === 'preserve'
              ? '90000000-0000-0000-0000-000000000001'
              : null,
        },
      });
    }
    if (path.endsWith('/version-control/stashes') && request.method() === 'GET')
      return route.fulfill({ json: stashes });
    if (
      path.includes('/version-control/stashes/') &&
      path.endsWith('/restore')
    ) {
      stashes.splice(0);
      galleryDirty = 1;
      return route.fulfill({
        json: {
          stashId: '90000000-0000-0000-0000-000000000001',
          operationId: 'op_restore_stash',
        },
      });
    }
    if (path.endsWith('/version-control/refs'))
      return route.fulfill({ json: refs });
    if (path.endsWith('/version-control/reflog'))
      return route.fulfill({
        json: [
          {
            id: '90000000-0000-0000-0000-000000000003',
            refName: 'source',
            oldHead: '3'.repeat(64),
            operation: 'rewrite',
            occurredAt: '2026-09-26T00:00:00Z',
            recoveryExpiresAt: '2099-09-26T00:00:00Z',
          },
        ],
      });
    if (path.endsWith('/version-control/rewrite/preview'))
      return route.fulfill({
        json: {
          branch: 'source',
          oldHead: '3'.repeat(64),
          newHead: url.searchParams.get('newHead'),
          displacedCommitIds: ['3'.repeat(64)],
          confirmationToken: 'fixture-preview',
        },
      });
    if (
      path.endsWith('/version-control/rewrite') &&
      request.method() === 'POST'
    )
      return route.fulfill({
        json: { commitId: '1'.repeat(64), operationId: 'op_rewrite' },
      });
    if (
      path.includes('/version-control/recovery/') &&
      request.method() === 'POST'
    )
      return route.fulfill({
        json: {
          name: request.postDataJSON().name,
          commitId: '3'.repeat(64),
          kind: 'branch',
          version: 1,
        },
      });
    if (path.includes('/version-control/working/'))
      return route.fulfill({
        json: webVersion
          ? [
              {
                id: '80000000-0000-0000-0000-000000000001',
                assetId: assetOne,
                editId: '30000000-0000-0000-0000-000000000001',
                branch: 'main',
                change: 'modified',
                baseContentSha256: xmpSha,
                contentSha256: 'c'.repeat(64),
                path: 'image.dng',
                lifecycle: 'active',
                version: webVersion,
                staged: false,
              },
            ]
          : [],
      });
    if (
      path.endsWith('/version-control/refs/branch') ||
      path.endsWith('/version-control/refs/tag')
    ) {
      const body = request.postDataJSON();
      const kind = path.endsWith('/tag') ? 'tag' : 'branch';
      refs.push({
        name: body.name,
        kind,
        commitId: body.commitId,
        protected: false,
        version: 1,
      });
      return route.fulfill({ json: refs.at(-1) });
    }
    if (
      path.includes('/version-control/refs/') &&
      request.method() === 'DELETE'
    ) {
      const name = decodeURIComponent(path.split('/').at(-1)!);
      const index = refs.findIndex((ref) => ref.name === name);
      if (index >= 0) refs.splice(index, 1);
      return route.fulfill({
        json: {
          refName: name,
          recoveryId: '90000000-0000-0000-0000-000000000002',
        },
      });
    }
    if (path.endsWith('/version-control/visibility'))
      return route.fulfill({ json: { operationId: 'op_visibility' } });
    if (path.includes('/version-control/staging/'))
      return route.fulfill({
        json: { id: '70000000-0000-0000-0000-000000000001', version: 1 },
      });
    if (
      path.endsWith('/version-control/commits') &&
      request.method() === 'POST'
    ) {
      webVersion = 0;
      return route.fulfill({
        json: { commitId: '8'.repeat(64), operationId: 'op_commit' },
      });
    }
    if (path.endsWith('/version-control/restore'))
      return route.fulfill({
        json: { commitId: '9'.repeat(64), operationId: 'op_restore' },
      });
    if (path.endsWith('/lineage/merges/prepare'))
      return route.fulfill({
        json: {
          targetBranch: 'main',
          sourceBranch: 'source',
          targetHead: '1'.repeat(64),
          sourceHead: '3'.repeat(64),
          baseCommitId: '2'.repeat(64),
          policyVersion: 'xmp-merge-v1',
          proposalDigest: '4'.repeat(64),
          affected: [
            {
              assetId: assetOne,
              editId: '30000000-0000-0000-0000-000000000001',
              baseXmpSha256: 'a'.repeat(64),
              targetXmpSha256: 'b'.repeat(64),
              sourceXmpSha256: 'c'.repeat(64),
              proposedXmpSha256: null,
              basePath: 'image.dng',
              targetPath: 'image.dng',
              sourcePath: 'image.dng',
              proposedPath: 'image.dng',
              baseLifecycle: 'active',
              targetLifecycle: 'active',
              sourceLifecycle: 'active',
              proposedLifecycle: 'active',
              conflicts: ['title'],
              metadataDifferences: [
                {
                  field: 'title',
                  base: 'old',
                  target: 'target title',
                  source: 'source title',
                  requiresResolution: true,
                },
              ],
              targetPreviewState: 'failed',
              sourcePreviewState: 'not_requested',
            },
          ],
          retained: [],
        },
      });
    if (path.endsWith('/lineage/merges/review'))
      return route.fulfill({
        json: {
          proposalDigest: '4'.repeat(64),
          reviewDigest: '5'.repeat(64),
          resolved: [
            {
              assetId: assetOne,
              editId: '30000000-0000-0000-0000-000000000001',
              xmpSha256: '6'.repeat(64),
              xmpBase64: btoa('<x:xmpmeta>reviewed</x:xmpmeta>'),
              path: 'image.dng',
              lifecycle: 'active',
              changed: true,
            },
          ],
          retained: [],
        },
      });
    if (path.endsWith('/lineage/merges/confirm'))
      return route.fulfill({
        json: { commitId: '7'.repeat(64), operationId: 'op_merge' },
      });
    if (path === '/v1/renditions' && request.method() === 'POST')
      return route.fulfill({
        json: {
          requestId: '20000000-0000-0000-0000-000000000001',
          state:
            String(request.postDataJSON().selectedVersion).length === 64
              ? 'pending'
              : 'completed',
          provenance: null,
        },
      });
    if (
      path.startsWith('/v1/renditions/') &&
      request.method() === 'GET' &&
      !path.endsWith('/derivative')
    )
      return route.fulfill({
        json: {
          requestId: '20000000-0000-0000-0000-000000000001',
          state: 'failed',
          provenance: null,
          failureCode: 'renderer_timeout',
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
    if (path.includes('/lineage/images/') && path.endsWith('/history')) {
      const visible = '1'.repeat(64);
      const hidden = '2'.repeat(64);
      const connector = '4'.repeat(64);
      const earlierConnector = '5'.repeat(64);
      const showHidden = url.searchParams.get('showHidden') === 'true';
      return route.fulfill({
        json: {
          assetId: assetOne,
          branch: 'main',
          headCommitId: visible,
          offset: 0,
          nextOffset: null,
          hasHiddenHistory: !showHidden,
          nodes: [
            {
              commit: {
                commitId: visible,
                parents: [connector],
                authorId: 'owner',
                authoredAt: '2026-09-26T00:00:00Z',
                message: 'Color edit',
                hidden: false,
              },
              branches: ['main'],
              tags: ['favorite'],
              hiddenParentIds: [],
            },
            ...(showHidden
              ? [
                  {
                    commit: {
                      commitId: hidden,
                      parents: [earlierConnector],
                      authorId: 'owner',
                      authoredAt: '2026-09-25T00:00:00Z',
                      message: 'Intermediate edit',
                      hidden: true,
                    },
                    branches: [],
                    tags: [],
                    hiddenParentIds: [],
                  },
                ]
              : []),
          ],
        },
      });
    }
    if (path.includes('/lineage/images/') && path.endsWith('/compare')) {
      const target = url.searchParams.get('target')!;
      const source = url.searchParams.get('source')!;
      const state = (commitId: string, sha: string) => ({
        commitId,
        assetId: assetOne,
        editId: '30000000-0000-0000-0000-000000000001',
        xmpSha256: sha,
        path: 'photos/image-1.jpg',
        lifecycle: 'active',
        previewState: 'not_requested',
        previewRequestId: null,
        previewProvenance: null,
      });
      return route.fulfill({
        json: {
          targetCommitId: target,
          sourceCommitId: source,
          baseCommitId: null,
          targetStates: [state(target, 'a'.repeat(64))],
          sourceStates: [state(source, 'b'.repeat(64))],
          differences: [
            {
              assetId: assetOne,
              editId: '30000000-0000-0000-0000-000000000001',
              baseContentSha256: null,
              oursContentSha256: 'a'.repeat(64),
              theirsContentSha256: 'b'.repeat(64),
              basePath: null,
              oursPath: 'photos/image-1.jpg',
              theirsPath: 'photos/image-1.jpg',
              baseLifecycle: null,
              oursLifecycle: 'active',
              theirsLifecycle: 'active',
            },
          ],
          metadataDifferences: [
            {
              editId: '30000000-0000-0000-0000-000000000001',
              state: 'no_common_ancestor',
              fields: [
                {
                  field: 'title',
                  base: 'Target title',
                  target: 'Target title',
                  source: 'Source title',
                  requiresResolution: false,
                },
              ],
            },
          ],
        },
      });
    }
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
