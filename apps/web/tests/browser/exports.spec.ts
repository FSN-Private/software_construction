import { expect, test, type Page } from '@playwright/test';
import { fixture, assetOne, image } from './fixtures';

async function exportsFixture(
  page: Page,
  options: { items?: number; pending?: boolean } = {},
) {
  await fixture(page, { exports: true });
  const pin = {
    itemId: 'selection-1',
    assetId: assetOne,
    editId: 'edit-1',
    commitId: 'c'.repeat(64),
    originalSha256: 'a'.repeat(64),
    originalByteLength: 4,
    xmpSha256: 'b'.repeat(64),
    displayName: 'image-1.jpg',
    versionLabel: 'Committed main',
    reference: 'main',
    advancedHistory: false,
    selectedAtUtc: '2026-10-04T00:00:00Z',
  };
  let baskets = [
    {
      id: 'basket-1',
      name: 'Saved selections',
      isDefault: true,
      revision: 1,
      items: Array.from({ length: options.items ?? 1 }, (_, index) => ({
        pin: { ...pin, itemId: `selection-${index + 1}` },
        availability: 'ready',
        previewState: 'not_requested',
        outputName: 'image-image-1-selection-1.jpg',
        reason: null,
      })),
    },
  ];
  const requests: unknown[] = [];
  if (options.pending)
    requests.push({
      id: 'pending-request',
      basketId: 'basket-1',
      state: 'pending',
      snapshot: { presetId: 'originals-v1', items: [pin] },
      outcomes: [{ itemId: pin.itemId, status: 'pending', reason: null }],
      packageSha256: null,
      expiresAtUtc: null,
    });
  await page.route('**/v1/libraries/*/exports/**', async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const path = url.pathname.split('/exports')[1];
    const method = req.method();
    if (path === '/limits')
      return route.fulfill({ json: { items: 250, baskets: 20, requests: 10 } });
    if (req.headers().authorization !== 'Bearer fixture-token')
      return route.fulfill({ status: 401, json: { code: 'unauthenticated' } });
    if (path === '/baskets' && method === 'GET')
      return route.fulfill({ json: baskets });
    if (path === '/requests') return route.fulfill({ json: requests });
    if (path === '/baskets' && method === 'POST') {
      const value = {
        id: 'basket-2',
        name: req.postDataJSON().name,
        isDefault: false,
        revision: 1,
        items: [],
      };
      baskets.push(value);
      return route.fulfill({ json: value });
    }
    if (path.endsWith('/requests') && method === 'POST') {
      const b = baskets.find((b) => path.includes(b.id))!;
      const input = req.postDataJSON();
      const result = {
        id: 'request-1',
        basketId: b.id,
        state: 'completed',
        snapshot: {
          presetId: input.presetId,
          items: b.items.map((i) => i.pin),
        },
        outcomes: b.items.map((i) => ({
          itemId: i.pin.itemId,
          status: 'completed',
          reason: null,
          filename: 'image-image-1-selection-1.jpg',
          sha256: 'a'.repeat(64),
          byteLength: 4,
        })),
        packageSha256: 'd'.repeat(64),
        packageByteLength: 11000000,
        expiresAtUtc: '2027-01-01T00:00:00Z',
        reason: null,
      };
      requests.push(result);
      return route.fulfill({ status: 202, json: result });
    }
    if (path.endsWith('/download-grants'))
      return route.fulfill({
        json: {
          code: 'e'.repeat(64),
          exchangeUrl:
            'https://downloads.example.test/v1/libraries/main/exports/requests/request-1/download-session',
        },
      });
    if (path.endsWith('/session/revoke')) return route.fulfill({ status: 204 });
    if (path.endsWith('/preview'))
      return route.fulfill({
        json: {
          state: 'completed',
          locator: image,
          provenance: { selectedVersion: pin.commitId },
        },
      });
    if (path.endsWith('/cancel')) {
      const request = requests.find((r) =>
        path.includes((r as { id: string }).id),
      ) as { state: string };
      request.state = 'cancelling';
      return route.fulfill({ json: request });
    }
    const b = baskets.find((b) => path.includes(b.id));
    if (b && path.endsWith('/items') && method === 'POST') {
      const selection = req.postDataJSON().selection;
      b.items.push({
        ...baskets[0].items[0],
        pin: {
          ...pin,
          itemId: `selection-${b.items.length + 1}`,
          assetId: selection.assetId,
          editId: selection.editId,
          reference: selection.selectedVersion,
          advancedHistory: !!selection.advancedHistory,
        },
      });
      b.revision++;
      return route.fulfill({ json: b });
    }
    if (b && method === 'PATCH') {
      const input = req.postDataJSON();
      b.name = input.name;
      if (input.isDefault)
        baskets.forEach((value) => (value.isDefault = value.id === b.id));
      b.revision++;
      return route.fulfill({ json: b });
    }
    if (b && method === 'DELETE') {
      if (path.endsWith('/items')) b.items = [];
      else if (path.includes('/items/'))
        b.items = b.items.filter((i) => !path.endsWith(i.pin.itemId));
      else baskets = baskets.filter((value) => value.id !== b.id);
      b.revision++;
      return route.fulfill({ json: b });
    }
    return route.fulfill({ status: 404, json: { code: 'export_not_found' } });
  });
}

test('persistent basket detail, privacy consent and native download exchange', async ({
  page,
}) => {
  await exportsFixture(page);
  await page.goto('/?rating=4&branch=main#id_token=fixture-token', {
    waitUntil: 'domcontentloaded',
  });
  await page
    .getByRole('button', { name: 'Export baskets', exact: true })
    .click();
  await expect(page.getByText('1 / 250 selections')).toBeVisible();
  await page
    .getByText('image-1.jpg · Committed main · Ready', { exact: true })
    .click();
  await page
    .getByRole('button', {
      name: 'Preview exact pinned version / refresh preview',
    })
    .click();
  await expect(
    page.getByRole('img', { name: 'Pinned version of image-1.jpg' }),
  ).toBeVisible();
  await page
    .getByRole('combobox', { name: 'Output preset' })
    .selectOption('originals-v1');
  await expect(
    page.getByRole('button', { name: 'Prepare immutable export' }),
  ).toBeDisabled();
  await page.getByLabel('I accept the embedded metadata disclosure.').focus();
  await page.keyboard.press('Space');
  await expect(
    page.getByLabel('I accept the embedded metadata disclosure.'),
  ).toBeChecked();
  await page.getByRole('button', { name: 'Prepare immutable export' }).click();
  await expect(page.getByText(/Captured 1 exact selections/)).toBeVisible();
  await expect(page.getByText(/ZIP: 11000000 bytes/)).toBeVisible();
  await page.evaluate(() => {
    HTMLFormElement.prototype.submit = function () {
      document.body.dataset.exchangeAction = this.action;
      document.body.dataset.exchangeMethod = this.method;
      document.body.dataset.exchangeFields = Array.from(
        new FormData(this).keys(),
      ).join(',');
    };
  });
  await page
    .getByRole('button', { name: 'Download ZIP / renew download session' })
    .click();
  await expect(page.locator('body')).toHaveAttribute(
    'data-exchange-action',
    /^https:\/\/downloads\.example\.test\/.*\/download-session$/,
  );
  await expect(page.locator('body')).toHaveAttribute(
    'data-exchange-method',
    'post',
  );
  await expect(page.locator('body')).toHaveAttribute(
    'data-exchange-fields',
    'code',
  );
  await page.getByRole('button', { name: 'Gallery', exact: true }).click();
  await expect(page).toHaveURL(/rating=4&branch=main/);
});

test('large basket cost consent, configured capacity and cancellation guidance', async ({
  page,
}) => {
  await exportsFixture(page, { items: 100, pending: true });
  await page.goto('/#id_token=fixture-token');
  await page
    .getByRole('button', { name: 'Export baskets', exact: true })
    .click();
  await expect(page.getByText('100 / 250 selections')).toBeVisible();
  await expect(
    page.getByRole('button', { name: 'Prepare immutable export' }),
  ).toBeDisabled();
  await page
    .getByLabel(
      'I accept the time, storage and transfer cost of this large export.',
    )
    .check();
  await expect(
    page.getByRole('button', { name: 'Prepare immutable export' }),
  ).toBeEnabled();
  await page.getByRole('button', { name: 'Cancel preparation' }).click();
  await expect(
    page.getByText(/Cancelling; storage cleanup pending/),
  ).toBeVisible();
});

test('gallery additions across three contexts preserve exact duplicate selections and browsing', async ({
  page,
}) => {
  await exportsFixture(page);
  await page.goto('/?rating=4#id_token=fixture-token');
  await page.getByLabel('Select image-1.jpg for export').check();
  await page
    .getByRole('button', { name: 'Add 1 selected images to basket' })
    .click();
  await page.getByLabel('Search in').selectOption('title');
  await page.getByLabel('Search photos').fill('Alpine');
  await page.getByLabel('Search photos').press('Enter');
  await page.getByLabel('Select image-1.jpg for export').check();
  await page
    .getByRole('button', { name: 'Add 1 selected images to basket' })
    .click();
  await page.getByRole('button', { name: 'Next page' }).click();
  await page.getByLabel('Select image-2.jpg for export').check();
  await page
    .getByRole('button', { name: 'Add 1 selected images to basket' })
    .click();
  await page
    .getByRole('button', { name: 'Export baskets', exact: true })
    .click();
  await expect(page.getByText('4 / 250 selections')).toBeVisible();
  await page.getByRole('button', { name: 'Gallery', exact: true }).click();
  await expect(page).toHaveURL(/title=Alpine/);
  await expect(page).toHaveURL(/cursor=page-two/);
});

test('keyboard basket create, rename, default and removal preserve request history', async ({
  page,
}) => {
  await exportsFixture(page);
  await page.goto('/#id_token=fixture-token', {
    waitUntil: 'domcontentloaded',
  });
  await page
    .getByRole('button', { name: 'Export baskets', exact: true })
    .click();
  await page.getByLabel('New basket name').fill('Travel exports');
  await page.getByRole('button', { name: 'Create basket' }).click();
  await expect(page.getByText('0 / 250 selections')).toBeVisible();
  await page
    .getByLabel('Basket name', { exact: true })
    .fill('Shared photographs');
  await page.getByLabel('Default basket', { exact: true }).focus();
  await page.keyboard.press('Space');
  await expect(
    page.getByLabel('Default basket', { exact: true }),
  ).toBeChecked();
  await page.getByRole('button', { name: 'Save basket' }).focus();
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('option', { name: 'Shared photographs (default) · 0' }),
  ).toHaveCount(1);
  await page.getByLabel('Saved basket').selectOption('basket-1');
  await page.getByRole('button', { name: /^Remove image-1.jpg/ }).click();
  await expect(page.getByText('0 / 250 selections')).toBeVisible();
});

test('locale catalog covers labels, reason codes and named interpolation with English fallback', async ({
  page,
}) => {
  await exportsFixture(page);
  await page.goto('/#id_token=fixture-token');
  const translated = await page.evaluate(async () => {
    const modulePath = '/src/export-messages.ts';
    const messages = await import(modulePath);
    messages.exportTranslations[navigator.language] = {
      'Export baskets': 'Test locale baskets',
      Ready: 'Test locale ready',
      'Remove {name} {id}': 'Test locale remove {id}: {name}',
    };
    window.dispatchEvent(new Event('exports-changed'));
    return messages.exportText(
      'Captured {count} exact selections in request {id}.',
      { count: 7, id: 'example' },
      'unknown-locale',
    );
  });
  expect(translated).toBe('Captured 7 exact selections in request example.');
  // Navigation causes App to render using the catalog loaded by the same module.
  await page
    .getByRole('button', { name: /^(Export baskets|Test locale baskets)$/ })
    .click();
  await expect(
    page.getByRole('heading', { name: 'Test locale baskets' }),
  ).toBeVisible();
  await expect(page.getByText(/Test locale ready/)).toBeVisible();
  await expect(
    page.getByRole('button', {
      name: 'Test locale remove selection-1: image-1.jpg',
    }),
  ).toBeVisible();
});

test('submission retry retains its key and sign-out waits for download revocation', async ({
  page,
}) => {
  await exportsFixture(page);
  const keys: string[] = [];
  await page.route('**/exports/baskets/*/requests', async (route) => {
    keys.push(route.request().headers()['idempotency-key']);
    if (keys.length === 1) return route.abort('failed');
    return route.fallback();
  });
  await page.goto('/#id_token=fixture-token');
  await page
    .getByRole('button', { name: 'Export baskets', exact: true })
    .click();
  await page.getByRole('button', { name: 'Prepare immutable export' }).click();
  await expect(
    page.getByRole('button', { name: 'Prepare immutable export' }),
  ).toBeEnabled();
  await page.getByRole('button', { name: 'Prepare immutable export' }).click();
  await expect(page.getByText(/Captured 1 exact selections/)).toBeVisible();
  expect(keys).toHaveLength(2);
  expect(keys[1]).toBe(keys[0]);
  await page.route('**/exports/session/revoke', (route) =>
    route.fulfill({ status: 503, json: { code: 'storage_unavailable' } }),
  );
  await page.getByText('Owner', { exact: true }).click();
  await page.getByRole('button', { name: 'Sign out', exact: true }).click();
  await expect(
    page.getByText(
      'Download-session revocation failed. Try signing out again.',
    ),
  ).toBeVisible();
  await expect(
    page.getByRole('heading', { name: 'Export baskets', exact: true }),
  ).toBeVisible();
});

test('clearing and deleting saved selections preserve immutable request evidence', async ({
  page,
}) => {
  await exportsFixture(page);
  await page.goto('/#id_token=fixture-token');
  await page
    .getByRole('button', { name: 'Export baskets', exact: true })
    .click();
  await page.getByRole('button', { name: 'Prepare immutable export' }).click();
  await expect(page.getByText(/Captured 1 exact selections/)).toBeVisible();
  await page.getByLabel('New basket name').fill('Replacement basket');
  await page.getByRole('button', { name: 'Create basket' }).click();
  await page.getByLabel('Saved basket').selectOption('basket-1');
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: 'Clear selections' }).click();
  await expect(page.getByText('0 / 250 selections')).toBeVisible();
  await expect(page.getByText(/ZIP: 11000000 bytes/)).toBeVisible();
  page.once('dialog', (dialog) => dialog.accept());
  await page.getByRole('button', { name: 'Delete basket' }).click();
  await expect(
    page.getByRole('option', { name: /Saved selections/ }),
  ).toHaveCount(0);
  await expect(page.getByText(/ZIP: 11000000 bytes/)).toBeVisible();
});

test('image history and explicit hidden lineage additions use the selected commit', async ({
  page,
}) => {
  await exportsFixture(page);
  const selections: {
    selectedVersion: string;
    advancedHistory?: boolean;
    editId: string;
  }[] = [];
  page.on('request', (request) => {
    if (
      request.method() === 'POST' &&
      /\/exports\/baskets\/[^/]+\/items$/.test(new URL(request.url()).pathname)
    )
      selections.push(request.postDataJSON().selection);
  });
  await page.goto(`/?asset=${assetOne}#id_token=fixture-token`);
  await page
    .getByRole('button', { name: 'Add this committed version to basket' })
    .click();
  await expect(
    page.getByText('Added exact committed version to the default basket.', {
      exact: true,
    }),
  ).toBeVisible();
  expect(selections[0]).toMatchObject({
    selectedVersion: '1'.repeat(64),
    editId: '30000000-0000-0000-0000-000000000001',
  });
  await page.getByRole('button', { name: 'View image history' }).click();
  await expect(
    page.getByRole('button', { name: /Intermediate edit/ }),
  ).toHaveCount(0);
  await page.getByLabel('Show hidden commits').check();
  await page.getByRole('button', { name: /Intermediate edit/ }).click();
  await page
    .getByRole('button', { name: /Add selected committed version to basket/ })
    .click();
  await expect(
    page.getByText('Added exact committed version to the default basket.', {
      exact: true,
    }),
  ).toBeVisible();
  expect(selections[1]).toMatchObject({
    selectedVersion: '2'.repeat(64),
    advancedHistory: true,
    editId: '30000000-0000-0000-0000-000000000001',
  });
});
