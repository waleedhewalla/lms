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

test('dashboard keeps the workspace layout', async ({ page }) => {
  await page.goto('/dashboard');
  await expect(page.locator('.sidebar')).toBeVisible();
  await expect(page.locator('.glass-card')).toHaveCount(6);
  await expect(page.getByRole('heading', { name: 'Directory', exact: true })).toBeVisible();
});

test('navigation to roles page works', async ({ page }) => {
  await page.goto('/dashboard');
  await page.getByRole('link', { name: '🛡️ Roles & RBAC' }).click();
  await expect(page).toHaveURL('/roles');
  await expect(page.getByText('Connection (dev)')).toBeVisible();
});
