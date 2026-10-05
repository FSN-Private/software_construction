import { expect, test } from '@playwright/test';
import { fixture } from './fixtures';

const dataClasses = [
  'originals',
  'xmp',
  'version_graph',
  'metadata',
  'audit',
  'configuration',
  'recovery_records',
];
function healthy() {
  return {
    status: 'healthy',
    observedAtUtc: new Date().toISOString(),
    classes: dataClasses.map((dataClass) => ({
      dataClass,
      state: 'verified',
      integrity: 'verified',
      coverage: 1,
      lastVerifiedAtUtc: new Date().toISOString(),
      recoveryPointAgeSeconds: 900,
    })),
    exceptions: [] as string[],
    lastAlertAtUtc: null,
  };
}

test('backup health is accessible and preserves gallery context', async ({
  page,
}) => {
  await fixture(page);
  await page.route('**/v1/recovery/health', (route) => {
    expect(route.request().headers().authorization).toBe(
      'Bearer fixture-token',
    );
    return route.fulfill({ json: healthy() });
  });
  await page.goto('/?camera=Sony#id_token=fixture-token');
  await page.getByLabel('Owner controls').click();
  await page.getByRole('button', { name: 'Backups', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Backups' })).toBeVisible();
  await expect(page.getByRole('status')).toContainText(
    'All required data classes',
  );
  await expect(page.getByRole('table', { name: 'Backups' })).toBeVisible();
  await expect(page.getByRole('row')).toHaveCount(8);
  await expect(
    page.getByRole('rowheader', { name: 'Original photographs' }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Refresh backup health' }).focus();
  await page.keyboard.press('Enter');
  await expect(
    page.getByRole('button', { name: 'Refresh backup health' }),
  ).toBeFocused();
  await expect(page.getByRole('status')).toContainText(
    'All required data classes',
  );
  await page.getByRole('button', { name: 'Gallery', exact: true }).click();
  await expect(page).toHaveURL(/camera=Sony/);
});

test('failed refresh clears successful protection and never displays provider details', async ({
  page,
}) => {
  await fixture(page);
  let fail = false;
  await page.route('**/v1/recovery/health', (route) =>
    route.fulfill(
      fail
        ? { status: 503, json: { detail: 'private-path-secret.jpg' } }
        : { json: healthy() },
    ),
  );
  await page.goto('/#id_token=fixture-token');
  await page.getByLabel('Owner controls').click();
  await page.getByRole('button', { name: 'Backups', exact: true }).click();
  await expect(page.getByRole('status')).toContainText(
    'All required data classes',
  );
  fail = true;
  await page.getByRole('button', { name: 'Refresh backup health' }).click();
  await expect(page.getByRole('alert')).toContainText('could not be verified');
  await expect(
    page.getByText('All required data classes', { exact: false }),
  ).toHaveCount(0);
  await expect(page.getByRole('table')).toHaveCount(0);
  await expect(page.getByText('private-path-secret.jpg')).toHaveCount(0);
});

test('missing or overdue classes never claim complete protection', async ({
  page,
}) => {
  await fixture(page);
  const response = healthy();
  response.classes.pop();
  response.classes[0].recoveryPointAgeSeconds = 7200;
  response.classes[0].state = 'overdue';
  response.exceptions.push('provider-secret/path');
  await page.route('**/v1/recovery/health', (route) =>
    route.fulfill({ json: response }),
  );
  await page.goto('/#id_token=fixture-token');
  await page.getByLabel('Owner controls').click();
  await page.getByRole('button', { name: 'Backups', exact: true }).click();
  await expect(page.getByRole('status')).toContainText('needs attention');
  await expect(
    page.getByRole('row').filter({ hasText: 'Recovery evidence' }),
  ).toContainText('Not verified');
  await expect(
    page.getByRole('row').filter({ hasText: 'Original photographs' }),
  ).toContainText('Overdue');
  await expect(page.getByText('provider-secret/path')).toHaveCount(0);
});
