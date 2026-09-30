import { expect, test } from '@playwright/test';
import { fixture } from './fixtures';

test('interactive local gallery preview', async ({ page }) => {
  await fixture(page, { performance: true });
  await page.goto('/#id_token=fixture-token');
  await expect(page.getByRole('heading', { name: 'All photos' })).toBeVisible();
  await expect(page.locator('.gallery-grid li')).toHaveCount(40);
  if (process.env.GALLERY_PREVIEW !== 'verify') await page.pause();
});
