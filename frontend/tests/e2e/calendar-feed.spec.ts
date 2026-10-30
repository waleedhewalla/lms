import { test, expect } from '@playwright/test';

// Needs a running API in Development (dev tokens on) and the production web build:
//   EDUNEXUS_E2E_API=http://localhost:5238 npx playwright test calendar-feed
const API = process.env.EDUNEXUS_E2E_API;
test.skip(!API, 'set EDUNEXUS_E2E_API to a development API to run this test');

test('make, use, replace and turn off a personal calendar link', async ({ page, request }) => {
  const dev = async (tenantId: string, permissions: string[], personId?: string) =>
    (await (await request.post(`${API}/api/auth/dev-token`, { data: { subject: 'e2e', tenantId, personId: personId ?? null, permissions } })).json()).token as string;
  const boot = await dev(crypto.randomUUID(), ['tenant:create']);
  const tenantId = (await (await request.post(`${API}/api/tenants`, { headers: { Authorization: `Bearer ${boot}` }, data: { slug: `cal-${Date.now().toString(36)}`, name: 'Cal University' } })).json()).id as string;
  const admin = await dev(tenantId, ['person:create', 'calendar:manage', 'calendar:read']);
  const person = (await (await request.post(`${API}/api/people`, { headers: { Authorization: `Bearer ${admin}` }, data: { tenantId, type: 'Employee', fullName: 'Layla' } })).json()).id as string;
  const start = new Date(Date.now() + 3 * 86400000).toISOString();
  await request.post(`${API}/api/calendar/events`, { headers: { Authorization: `Bearer ${admin}` }, data: { tenantId, title: 'Founders Day', kind: 'Holiday', startsAt: start, endsAt: start } });
  const token = await dev(tenantId, ['calendar:read'], person);

  await page.goto('/signin');
  await page.evaluate(([tk, tn]) => { localStorage.setItem('edunexus.token', tk); localStorage.setItem('edunexus.tenant', tn); }, [token, tenantId]);
  await page.goto('/calendar');
  await page.getByRole('button', { name: 'Load', exact: true }).click();

  await page.getByRole('button', { name: 'Create my link' }).click();
  const field = page.getByLabel('Your calendar link');
  await expect(field).toHaveValue(new RegExp(`^https?://[^/]+/api/calendar/feed/${tenantId.replace(/-/g, '')}[A-Za-z0-9_-]{43}\\.ics$`));
  const first = await field.inputValue();

  // The link works for anyone holding it, with no sign-in, and shows the institution's dates.
  const feed = await request.get(first);
  expect(feed.status()).toBe(200);
  expect(await feed.text()).toContain('Founders Day');

  // Shown once: after a reload only the status remains, not the link.
  await page.reload();
  await page.getByRole('button', { name: 'Load', exact: true }).click();
  await expect(page.getByText(/A link is active until/)).toBeVisible();
  await expect(page.getByLabel('Your calendar link')).toHaveCount(0);

  // A new link replaces the old one.
  await page.getByRole('button', { name: 'Make a new link' }).click();
  const second = await page.getByLabel('Your calendar link').inputValue();
  expect(second).not.toBe(first);
  expect((await request.get(first)).status()).toBe(404);
  expect((await request.get(second)).status()).toBe(200);

  // Turning it off stops it.
  await page.getByRole('button', { name: 'Turn off' }).click();
  await expect(page.getByRole('button', { name: 'Create my link' })).toBeVisible();
  expect((await request.get(second)).status()).toBe(404);
});
