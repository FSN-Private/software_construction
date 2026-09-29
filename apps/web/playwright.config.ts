import { defineConfig } from '@playwright/test';

const browserTestPort = Number(process.env.BROWSER_TEST_PORT ?? 4173);
const browserTestOrigin = `http://127.0.0.1:${browserTestPort}`;

export default defineConfig({
  testDir: './tests/browser',
  timeout: 30_000,
  workers: 1,
  expect: { timeout: 5_000 },
  use: { baseURL: browserTestOrigin, headless: true },
  projects: [
    {
      name: 'Chrome for Testing',
      use: { browserName: 'chromium', channel: 'chromium' },
    },
    {
      name: 'Microsoft Edge',
      use: { browserName: 'chromium', channel: 'msedge' },
    },
    { name: 'Firefox', use: { browserName: 'firefox' } },
  ],
  webServer: process.env.BROWSER_TEST_EXTERNAL_SERVER
    ? undefined
    : {
        command: `npx vite --host 127.0.0.1 --port ${browserTestPort} --strictPort`,
        url: browserTestOrigin,
        reuseExistingServer: !process.env.CI,
        timeout: 30_000,
      },
});
