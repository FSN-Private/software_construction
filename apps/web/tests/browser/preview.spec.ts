import { expect, test } from '@playwright/test';
import { fixture } from './fixtures';

test.use({ viewport: null });

test('interactive local gallery preview', async ({ page }) => {
  await fixture(page, { performance: true, preview: true });
  await page.goto('/#id_token=fixture-token');
  await expect(page.getByRole('heading', { name: 'All photos' })).toBeVisible();
  await expect(page.locator('.gallery-grid li')).toHaveCount(40);
  expect(page.viewportSize()).toBeNull();
  await expect(page.getByRole('button', { name: 'Next page' })).toBeEnabled();
  await page.getByRole('button', { name: 'Next page' }).click();
  await expect(page.getByText('41–80 of 100,000')).toBeVisible();
  await expect(
    page.getByRole('button', { name: /image-41.jpg/ }),
  ).toBeVisible();
  await page.getByRole('button', { name: 'Previous page' }).click();
  await expect(page.getByText('1–40 of 100,000')).toBeVisible();
  if (process.env.GALLERY_PREVIEW === 'verify') {
    for (const width of [960, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      const gallery = await page.locator('.gallery').boundingBox();
      expect(gallery?.width).toBe(width);
      expect(
        await page.evaluate(() => document.documentElement.scrollWidth),
      ).toBe(width);
    }
  }
  if (process.env.GALLERY_PREVIEW !== 'verify') {
    test.setTimeout(0);
    await page.waitForTimeout(3_600_000);
  }
});
