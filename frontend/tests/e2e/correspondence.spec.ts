import { test, expect, type APIRequestContext } from '@playwright/test';

// Needs a running API in Development (dev tokens on) and the production web build:
//   EDUNEXUS_E2E_API=http://localhost:5238 npx playwright test correspondence
const API = process.env.EDUNEXUS_E2E_API;
test.skip(!API, 'set EDUNEXUS_E2E_API to a development API to run this test');

const PERMS = ['correspondence:create', 'correspondence:read', 'correspondence:confidential', 'person:create', 'person:read', 'approval:read', 'approval:decide', 'task:read'];

async function mint(request: APIRequestContext, tenantId: string, permissions: string[], personId?: string) {
  const r = await request.post(`${API}/api/auth/dev-token`, { data: { subject: 'e2e', tenantId, personId: personId ?? null, permissions } });
  expect(r.ok()).toBeTruthy();
  return (await r.json()).token as string;
}

test('write, register, send for review, direct and export a letter on the real API', async ({ page, request }) => {
  // A fresh institution with an author and a reviewer.
  const boot = await mint(request, crypto.randomUUID(), ['tenant:create']);
  const t = await request.post(`${API}/api/tenants`, { headers: { Authorization: `Bearer ${boot}` }, data: { slug: `e2e-${Date.now().toString(36)}`, name: 'E2E University' } });
  const tenantId = (await t.json()).id as string;
  const admin = await mint(request, tenantId, PERMS);
  const person = async (fullName: string) => (await (await request.post(`${API}/api/people`, { headers: { Authorization: `Bearer ${admin}` }, data: { tenantId, type: 'Employee', fullName } })).json()).id as string;
  const author = await person('Karim Author');
  await person('Rana Reviewer');
  const token = await mint(request, tenantId, PERMS, author);

  await page.goto('/signin');
  await page.evaluate(([tk, tn]) => { localStorage.setItem('edunexus.token', tk); localStorage.setItem('edunexus.tenant', tn); }, [token, tenantId]);
  await page.goto('/correspondence');
  await expect(page.getByText('No letters match.')).toBeVisible();

  // New outgoing letter with an external addressee and a copy.
  await page.getByRole('button', { name: '+ New letter' }).click();
  await page.getByLabel('Direction', { exact: true }).first().selectOption('Outgoing');
  await page.getByLabel('Subject', { exact: true }).fill('Reply to the Ministry');
  await page.getByLabel('Content', { exact: true }).fill('We confirm the dates.');
  await page.getByLabel('External party name').fill('Ministry of Education');
  await page.getByRole('button', { name: 'Add external' }).click();
  await page.getByLabel('To or copy').selectOption('Cc');
  await page.getByLabel('External party name').fill('Legal Office');
  await page.getByRole('button', { name: 'Add external' }).click();
  await page.getByRole('button', { name: 'Save draft' }).click();

  const year = new Date().getFullYear();
  await expect(page.getByRole('heading', { name: new RegExp(`OUT-${year}-000001`) })).toBeVisible();
  await expect(page.locator('tbody tr', { hasText: 'Reply to the Ministry' })).toContainText('Ministry of Education');
  await expect(page.locator('tbody tr', { hasText: 'Reply to the Ministry' })).toContainText('Legal Office');

  // Send for review to the reviewer found in the directory.
  await page.getByLabel('Find the reviewer').fill('Rana');
  const reviewerPicker = page.getByLabel('Find the reviewer').locator('..').locator('..');
  await reviewerPicker.getByRole('button', { name: 'Search' }).click();
  await reviewerPicker.getByRole('button', { name: 'Rana Reviewer' }).click();
  await page.getByRole('button', { name: 'Submit', exact: true }).click();
  await expect(page.locator('tbody tr', { hasText: 'Reply to the Ministry' })).toContainText('Submitted');

  // A directive, then the register export.
  await page.getByLabel('Instructions', { exact: true }).fill('Prepare the attachments.');
  await page.getByRole('button', { name: 'Add directive' }).click();
  await expect(page.getByRole('cell', { name: 'Prepare the attachments.' })).toBeVisible();

  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: /Export register/ }).click()]);
  const csv = (await (await import('node:fs/promises')).readFile((await download.path())!, 'utf8'));
  expect(csv).toContain(`OUT-${year}-000001`);
  expect(csv).toContain('created_at_hijri');
  expect(csv).toContain('Legal Office');
});
