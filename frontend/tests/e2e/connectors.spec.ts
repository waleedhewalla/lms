import { test, expect } from '@playwright/test';

// Needs a development API started with Connectors__Enabled=true and a Connectors__SecretKey, and the production web build:
//   EDUNEXUS_E2E_API=http://localhost:5238 EDUNEXUS_E2E_CONNECTORS=1 npx playwright test connectors
const API = process.env.EDUNEXUS_E2E_API;
test.skip(!API || !process.env.EDUNEXUS_E2E_CONNECTORS, 'needs EDUNEXUS_E2E_API and an API with Connectors__Enabled=true');

// Next's own route announcer also has role=alert; this is the page's error box.
const alert = (page: import('@playwright/test').Page) => page.locator('[role=alert]:not(#__next-route-announcer__)');

test('add a file connection, load a course list, search it, and see the server explain a refused address and an unreachable system', async ({ page, request }) => {
  const dev = async (tenantId: string, permissions: string[]) =>
    (await (await request.post(`${API}/api/auth/dev-token`, { data: { subject: 'e2e', tenantId, personId: null, permissions } })).json()).token as string;
  const boot = await dev(crypto.randomUUID(), ['tenant:create']);
  const tenantId = (await (await request.post(`${API}/api/tenants`, { headers: { Authorization: `Bearer ${boot}` }, data: { slug: `con-${Date.now().toString(36)}`, name: 'Connect University' } })).json()).id as string;
  const token = await dev(tenantId, ['integration:read', 'integration:manage']);

  await page.goto('/signin');
  await page.evaluate(([tk, tn]) => { localStorage.setItem('edunexus.token', tk); localStorage.setItem('edunexus.tenant', tn); }, [token, tenantId]);
  await page.goto('/connectors');
  await expect(page.getByText('No connections yet.')).toBeVisible();

  // A file connection and its upload.
  await page.getByLabel('System', { exact: true }).selectOption('Csv');
  await page.getByLabel('Name', { exact: true }).fill('Banner export');
  await page.getByRole('button', { name: 'Save connection' }).click();
  await expect(page.getByText('Connection saved.')).toBeVisible();
  await expect(page.locator('tbody tr', { hasText: 'Banner export' })).toContainText('File (CSV)');

  const upload = (name: string, csv: string) => page.getByLabel('Upload CSV for Banner export').setInputFiles({ name, mimeType: 'text/csv', buffer: Buffer.from(csv) });
  await upload('bad.csv', 'code,title,enrolled\nENG101,English,40\nMAT101,,x\n');
  await expect(alert(page)).toContainText('Row 3');
  await upload('courses.csv', 'code,title,term,enrolled,instructors\nENG101,English Composition,Fall 2026,40,Layla Haddad; Omar Saleh\nMAT101,Calculus,Fall 2026,35,Rana Aziz\n');
  await expect(page.getByText(/Banner export: 2 course\(s\) — 2 new/)).toBeVisible();
  await expect(page.locator('tbody tr', { hasText: 'ENG101' })).toContainText('Layla Haddad; Omar Saleh');

  await page.getByLabel('Search code, title or instructor').fill('rana');
  await page.getByRole('button', { name: 'Apply' }).click();
  await expect(page.locator('tbody tr', { hasText: 'MAT101' })).toBeVisible();
  await expect(page.locator('tbody tr', { hasText: 'ENG101' })).toHaveCount(0);

  // A live connection to an internal address is refused, with the server's own words.
  await page.getByLabel('System', { exact: true }).selectOption('Moodle');
  await page.getByLabel('Name', { exact: true }).fill('Internal Moodle');
  await page.getByLabel('Server address').fill('http://169.254.169.254/latest');
  await page.getByLabel(/Web-service token/).fill('secret-token-value');
  await page.getByRole('button', { name: 'Save connection' }).click();
  await expect(alert(page)).toContainText('loopback, private or link-local');

  // A reachable-looking address is saved; reading it fails plainly (no real server here), and the credential is not shown anywhere.
  await page.getByLabel('Server address').fill('https://moodle.invalid');
  await page.getByRole('button', { name: 'Save connection' }).click();
  await expect(page.getByText('Connection saved.')).toBeVisible();
  await page.locator('tbody tr', { hasText: 'Internal Moodle' }).getByRole('button', { name: 'Read now' }).click();
  await expect(alert(page)).toContainText('Moodle:');
  await expect(page.locator('body')).not.toContainText('secret-token-value');
  await expect(page.locator('tbody tr', { hasText: 'Internal Moodle' })).toContainText('⚠');   // the last read is shown as failed

  // Deleting removes the connection and the courses it brought.
  page.once('dialog', (d) => d.accept());
  await page.locator('tbody tr', { hasText: 'Banner export' }).getByRole('button', { name: 'Delete' }).click();
  await expect(page.locator('tbody tr', { hasText: 'Banner export' })).toHaveCount(0);
  await page.getByLabel('Search code, title or instructor').fill('');
  await page.getByRole('button', { name: 'Apply' }).click();
  await expect(page.locator('tbody tr', { hasText: 'MAT101' })).toHaveCount(0);
});
