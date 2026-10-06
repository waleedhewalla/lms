import { test, expect } from '@playwright/test';

test('homepage loads and shows layout', async ({ page }) => {
  await page.goto('/');

  // Verify the layout shell is present
  await expect(page.locator('.sidebar')).toBeVisible();
  await expect(page.getByText('EduNexus OS', { exact: true })).toBeVisible();

  // Verify the dashboard renders cards
  await expect(page.locator('.glass-card')).toHaveCount(6);
  
  // Verify specific modules
  await expect(page.getByRole('heading', { name: 'Directory', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Roles & Assignments', exact: true })).toBeVisible();
});

test('navigation to roles page works', async ({ page }) => {
  await page.goto('/');
  
  // Click on the Roles navigation link in the sidebar
  await page.getByRole('link', { name: '🛡️ Roles & RBAC' }).click();

  // Ensure we navigate correctly
  await expect(page).toHaveURL('/roles');
  
  // Ensure the Connection dev token input exists
  await expect(page.getByText('Connection (dev)')).toBeVisible();
});
