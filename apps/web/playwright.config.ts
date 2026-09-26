import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/browser',
  timeout: 30_000,
  workers: 1,
  expect: { timeout: 5_000 },
  use: { baseURL: 'http://127.0.0.1:4173', headless: true },
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
  webServer: {
    command: 'npx vite --host 127.0.0.1 --port 4173 --strictPort',
    url: 'http://127.0.0.1:4173',
    reuseExistingServer: !process.env.CI,
    timeout: 30_000,
  },
});
