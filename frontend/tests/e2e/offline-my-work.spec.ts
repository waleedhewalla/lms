import { test, expect, type APIRequestContext } from '@playwright/test';

// Needs a development API and a production web build started with EDUNEXUS_OFFLINE_DATA=true:
//   EDUNEXUS_E2E_API=http://localhost:5238 EDUNEXUS_E2E_OFFLINE=1 npx playwright test offline-my-work
const API = process.env.EDUNEXUS_E2E_API;
test.skip(!API || !process.env.EDUNEXUS_E2E_OFFLINE, 'needs EDUNEXUS_E2E_API and a web build started with EDUNEXUS_OFFLINE_DATA=true');

const ADMIN = ['tenant:create', 'person:create', 'person:read', 'correspondence:create', 'correspondence:read', 'task:create', 'task:read'];
const ME = ['inbox:read', 'approval:read', 'approval:decide', 'task:read', 'task:update', 'notification:read'];

async function dev(request: APIRequestContext, tenantId: string, permissions: string[], personId?: string, subject = 'e2e') {
  const r = await request.post(`${API}/api/auth/dev-token`, { data: { subject, tenantId, personId: personId ?? null, permissions } });
  return (await r.json()).token as string;
}
const idbKeys = (page: import('@playwright/test').Page) => page.evaluate(() => new Promise<string[]>((resolve) => {
  const open = indexedDB.open('edunexus-offline');
  open.onerror = () => resolve([]);
  open.onsuccess = () => {
    const db = open.result;
    if (!db.objectStoreNames.contains('kv')) { db.close(); resolve([]); return; }
    const req = db.transaction('kv').objectStore('kv').getAllKeys();
    req.onsuccess = () => { db.close(); resolve(req.result.map(String)); };
  };
}));

test('read and act offline, review and send later, and leave nothing behind after sign-out', async ({ page, context, request }) => {
  // An institution where Layla has two letters to approve and one task.
  const boot = await dev(request, crypto.randomUUID(), ['tenant:create']);
  const tenantId = (await (await request.post(`${API}/api/tenants`, { headers: { Authorization: `Bearer ${boot}` }, data: { slug: `off-${Date.now().toString(36)}`, name: 'Offline University' } })).json()).id as string;
  const admin = await dev(request, tenantId, ADMIN);
  const H = { Authorization: `Bearer ${admin}` };
  const person = async (fullName: string) => (await (await request.post(`${API}/api/people`, { headers: H, data: { tenantId, type: 'Employee', fullName } })).json()).id as string;
  const [layla, boss] = [await person('Layla Reviewer'), await person('Boss Author')];
  const letters: string[] = [];
  for (const subject of ['First letter', 'Second letter']) {
    const c = await (await request.post(`${API}/api/correspondence`, { headers: H, data: { tenantId, type: 'Internal', subject, content: 'Body', authorId: boss, priority: 'Normal', isConfidential: false, recipients: [] } })).json();
    await request.post(`${API}/api/correspondence/${c.id}/submit`, { headers: H, data: { tenantId, reviewerId: layla } });
    letters.push(c.id);
  }
  await request.post(`${API}/api/tasks`, { headers: H, data: { tenantId, title: 'Prepare the report', assigneeId: layla, dueAt: new Date(Date.now() + 5 * 86400000).toISOString() } });
  const mine = await dev(request, tenantId, ME, layla);
  const approvalIds = async () => (await (await request.get(`${API}/api/approvals?tenantId=${tenantId}&assigneeId=${layla}`, { headers: { Authorization: `Bearer ${mine}` } })).json()) as { id: string; status: string; entityId: string }[];

  // Online: the page loads and a copy is kept on the device.
  await page.goto('/signin');
  await page.evaluate(([tk, tn]) => { localStorage.setItem('edunexus.token', tk); localStorage.setItem('edunexus.tenant', tn); }, [mine, tenantId]);
  await page.goto('/my-work');
  await expect(page.locator('tbody tr', { hasText: 'Correspondence' })).toHaveCount(2);
  await expect.poll(() => idbKeys(page)).toContain('snapshot');
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.reload();                                        // now the page is served through the worker, which keeps its shell
  await expect(page.locator('tbody tr', { hasText: 'Correspondence' })).toHaveCount(2);

  // Offline, with the short-lived token already expired: the saved copy opens and the person can still act on it.
  const expired = await page.evaluate(() => {
    const [h, p, s] = (localStorage.getItem('edunexus.token') as string).split('.');
    const payload = JSON.parse(atob(p.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (p.length % 4)) % 4)));
    payload.exp = Math.floor(Date.now() / 1000) - 60;
    return `${h}.${btoa(JSON.stringify(payload)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')}.${s}`;
  });
  await page.evaluate((t) => localStorage.setItem('edunexus.token', t), expired);
  await context.setOffline(true);
  await page.reload();
  await expect(page.getByText('You are offline.')).toBeVisible();
  await expect(page.locator('tbody tr', { hasText: 'Correspondence' })).toHaveCount(2);
  await page.locator('tbody tr', { hasText: 'Correspondence' }).first().getByRole('button', { name: 'Approve' }).click();
  await page.locator('tbody tr', { hasText: 'Correspondence' }).first().getByRole('button', { name: 'Approve' }).click();
  await expect(page.getByText('Waiting to be sent (2)')).toBeVisible();
  await expect(page.locator('tbody tr', { hasText: 'Correspondence' })).toHaveCount(0);   // queued ones leave the list
  expect((await approvalIds()).every((a) => a.status === 'Pending')).toBeTruthy();       // nothing reached the server

  // Someone else decides the second letter's approval in the meantime.
  const all = await approvalIds();
  const second = all.find((a) => a.entityId === letters[1])!;
  await request.post(`${API}/api/approvals/${second.id}/decide`, { headers: { Authorization: `Bearer ${mine}` }, data: { tenantId, approve: false, comment: 'decided elsewhere' } });

  // Back online and signed in again as the same person: nothing is sent until she says so, then the server re-checks each one.
  await context.setOffline(false);
  await page.evaluate((t) => localStorage.setItem('edunexus.token', t), mine);
  await page.reload();
  await expect(page.getByText('Waiting to be sent (2)')).toBeVisible();
  expect((await approvalIds()).filter((a) => a.status === 'Approved')).toHaveLength(0);
  await page.getByRole('button', { name: 'Send now' }).click();
  await expect(page.getByText('Sent: 1')).toBeVisible();
  await expect(page.getByText(/already decided or completed by someone else/)).toBeVisible();
  const after = await approvalIds();
  expect(after.find((a) => a.entityId === letters[0])!.status).toBe('Approved');
  expect(after.find((a) => a.entityId === letters[1])!.status).toBe('Rejected');            // the other decision stands
  await expect(page.getByText(/Waiting to be sent/)).toHaveCount(0);

  // Signing out removes the copy and the queue.
  expect((await idbKeys(page)).length).toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Sign out' }).click();
  await page.waitForURL('**/');
  await expect.poll(() => idbKeys(page)).toEqual([]);
});
