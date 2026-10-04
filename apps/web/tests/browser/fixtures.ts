import { type Page } from '@playwright/test';

export const browserTestOrigin = `http://127.0.0.1:${process.env.BROWSER_TEST_PORT ?? 4173}`;

export const originalSha = 'a'.repeat(64);
export const xmpSha = 'b'.repeat(64);
export const image =
  'data:image/svg+xml,%3Csvg xmlns="http://www.w3.org/2000/svg" width="8" height="8"%3E%3Crect width="8" height="8" fill="gray"/%3E%3C/svg%3E';
export const assetOne = '10000000-0000-0000-0000-000000000001';
export const assetTwo = '10000000-0000-0000-0000-000000000002';

export function card(index: number) {
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

export function detail(assetId: string, webVersion: number) {
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
        name: 'tag',
        text: 'Landscape',
        number: null,
        date: null,
        boolean: null,
        source: 'xmp',
        state: 'present',
        timestampBasis: null,
      },
      ...[
        ['camera', 'Sony A7 IV'],
        ['lens', '24-70 mm'],
      ].map(([name, text]) => ({
        name,
        text,
        number: null,
        date: null,
        boolean: null,
        source: 'embedded',
        state: 'present',
        timestampBasis: null,
      })),
      ...[
        ['shutter', 0.004],
        ['aperture', 8],
        ['iso', 100],
        ['width', 6000],
        ['height', 4000],
        ['rating', 4],
      ].map(([name, value]) => ({
        name: String(name),
        text: null,
        number: Number(value),
        date: null,
        boolean: null,
        source: 'embedded',
        state: 'present',
        timestampBasis: null,
      })),

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

export async function fixture(
  page: Page,
  options: {
    performance?: boolean;
    previewFailed?: boolean;
    preview?: boolean;
    exports?: boolean;
  } = {},
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
      body: `window.__PLATFORM_CONFIG__={apiBaseUrl:"${browserTestOrigin}",cognitoHostedUiUrl:"${browserTestOrigin}",cognitoClientId:"fixture",redirectUri:"${browserTestOrigin}",galleryLibraryId:"main",previewMode:${!!options.preview},exportsEnabled:${!!options.exports}};`,
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
    if (path.endsWith('/gallery/filter-values')) {
      const choices: Record<string, string[]> = {
        camera: ['Sony', 'Original', 'Slow', 'Fast', 'Fixture camera'],
        lens: ['24-70'],
        fileType: ['dng'],
        rating: ['4', '5'],
        orientation: ['1'],
        tag: ['landscape', 'portrait'],
        color: ['red'],
        focalLength: ['35', '50'],
        aperture: ['2.8'],
        shutter: ['0.008'],
        iso: ['100', '400'],
        width: ['4000'],
        height: ['3000'],
        latitude: ['40.5'],
        longitude: ['-74.25'],
      };
      if (
        preference.hideGeolocation &&
        ['latitude', 'longitude'].includes(url.searchParams.get('facet') ?? '')
      )
        return route.fulfill({
          status: 400,
          json: { code: 'location_hidden' },
        });
      const query = url.searchParams.get('q')?.toLowerCase() ?? '';
      return route.fulfill({
        json: {
          values: (choices[url.searchParams.get('facet') ?? ''] ?? [])
            .filter((value) => value.toLowerCase().includes(query))
            .map((value) => ({ value, label: value })),
          hasMore: false,
        },
      });
    }
    if (path.endsWith('/gallery')) {
      if (options.performance)
        await new Promise((resolve) => setTimeout(resolve, 504));
      const offset = Number(url.searchParams.get('cursor') ?? 0);
      const cards = options.performance
        ? Array.from({ length: 40 }, (_, index) => card(offset + index + 1))
        : url.searchParams.has('cursor')
          ? [card(2)]
          : [card(1)];
      if (options.previewFailed)
        for (const item of cards) item.previewState = 'failed';
      return route.fulfill({
        json: {
          cards,
          count: options.performance ? 100_000 : 2,
          nextCursor: options.performance
            ? offset + cards.length < 100_000
              ? String(offset + cards.length)
              : null
            : url.searchParams.has('cursor')
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
