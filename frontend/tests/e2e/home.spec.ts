import { test, expect } from '@playwright/test';

test('public home page shows the product and a sign-in entry', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.sidebar')).toHaveCount(0);
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Governance, quality and daily work');
  await expect(page.locator('.pub-header').getByRole('link', { name: 'Sign in' })).toBeVisible();
  await expect(page.locator('#features .pub-feature')).toHaveCount(12);
});

test('sign-in icon opens the sign-in page with role choices', async ({ page }) => {
  await page.goto('/');
  await page.locator('.pub-header').getByRole('link', { name: 'Sign in' }).click();
  await expect(page).toHaveURL('/signin');
  await expect(page.getByRole('heading', { name: 'Sign in to EduNexus' })).toBeVisible();
  await expect(page.getByRole('radio')).toHaveCount(4);
});

/** A signed-in session for the browser: an unsigned token is enough for the UI (the API checks real ones). */
async function signInAs(page: import('@playwright/test').Page, permission: string[]) {
  const payload = Buffer.from(JSON.stringify({ sub: 'e2e', name: 'E2E User', permission, exp: Math.floor(Date.now() / 1000) + 3600 })).toString('base64url');
  await page.goto('/signin');
  await page.evaluate((t) => localStorage.setItem('edunexus.token', t), `x.${payload}.y`);
}

test('workspace pages send signed-out visitors to sign in', async ({ page }) => {
  await page.goto('/dashboard');
  await expect(page).toHaveURL(/\/signin\?next=%2Fdashboard/);
});

test('dashboard shows only the modules the account can open', async ({ page }) => {
  await signInAs(page, ['inbox:read', 'meeting:read', 'policy:read']);
  await page.goto('/dashboard');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('E2E User');
  await expect(page.locator('.ws-module')).toHaveCount(5); // My Work, Meetings, Board, Policies, Settings
  await expect(page.locator('.sidebar').getByRole('link', { name: 'Records Retention' })).toHaveCount(0);
});

test('a module the account cannot open says so', async ({ page }) => {
  await signInAs(page, ['inbox:read']);
  await page.goto('/records');
  await expect(page.getByRole('alert')).toContainText("You don't have access to this module");
});
