import { test, expect } from '@playwright/test';

test('failed login request releases the button and allows retry', async ({ page }) => {
  if (!process.env.SMOKE_PASSWORD) throw new Error('SMOKE_PASSWORD is required');
  await page.goto('/#/login');
  await page.locator('input[autocomplete="username"]').fill(process.env.SMOKE_USERNAME || 'admin');
  await page.locator('input[autocomplete="current-password"]').fill(process.env.SMOKE_PASSWORD);
  await page.route('**/api/Auth/Login', route => route.abort('failed'));
  await page.getByRole('button', { name: '登 录' }).click();
  await expect(page.locator('form p')).toContainText('网络请求失败');
  await expect(page.getByRole('button', { name: '登 录' })).toBeEnabled();
  await page.unroute('**/api/Auth/Login');
  await page.getByRole('button', { name: '登 录' }).click();
  await expect(page).toHaveURL(/\/#\/$/);
});

test('login, protected pages and logout work in a real browser', async ({ page }) => {
  if (!process.env.SMOKE_PASSWORD) throw new Error('SMOKE_PASSWORD is required');
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => {
    if (response.url().includes('/api/') && response.status() >= 400) errors.push(`HTTP ${response.status()} ${new URL(response.url()).pathname}`);
  });
  await page.goto('/#/login');
  await page.locator('input[autocomplete="username"]').fill(process.env.SMOKE_USERNAME || 'admin');
  await page.locator('input[autocomplete="current-password"]').fill(process.env.SMOKE_PASSWORD);
  await page.getByRole('button', { name: '登 录' }).click();
  await expect(page).toHaveURL(/\/#\/$/);
  for (const path of ['/commands', '/easytier', '/logs']) {
    const response = page.waitForResponse(r => r.url().includes('/api/') && r.request().method() === 'GET' && r.status() === 200);
    await page.goto('/#' + path);
    await response;
    await expect(page.locator('main')).toBeVisible();
  }
  await page.getByRole('button', { name: '退出', exact: true }).click();
  await expect(page).toHaveURL(/#\/login$/);
  await expect(page.locator('input[autocomplete="current-password"]')).toBeVisible();
  expect(errors).toEqual([]);
});
