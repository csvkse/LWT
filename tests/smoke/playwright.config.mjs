import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir: '.', testMatch: 'browser.spec.mjs', timeout: 45000,
  expect: { timeout: 10000 }, retries: 0, workers: 1,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { channel: process.env.SMOKE_BROWSER_CHANNEL || 'chromium', baseURL: process.env.SMOKE_URL || 'http://127.0.0.1:15270', screenshot: 'only-on-failure', trace: 'retain-on-failure' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'] } },
    { name: 'mobile', use: { ...devices['Pixel 7'], defaultBrowserType: 'chromium' } },
  ],
});
