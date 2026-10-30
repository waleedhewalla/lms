import { test, expect, type APIRequestContext } from '@playwright/test';

// Needs a running API in Development (dev tokens on) and the production web build:
//   EDUNEXUS_E2E_API=http://localhost:5238 npx playwright test correspondence-workflow
const API = process.env.EDUNEXUS_E2E_API;
test.skip(!API, 'set EDUNEXUS_E2E_API to a development API to run this test');

async function mint(request: APIRequestContext, tenantId: string, permissions: string[], personId?: string) {
  const r = await request.post(`${API}/api/auth/dev-token`, { data: { subject: 'e2e', tenantId, personId: personId ?? null, permissions } });
  expect(r.ok()).toBeTruthy();
  return (await r.json()).token as string;
}

test('a typed letter goes to the head, comes back for changes, is edited, sent again and then withdrawn', async ({ page, request }) => {
  const boot = await mint(request, crypto.randomUUID(), ['tenant:create']);
  const t = await request.post(`${API}/api/tenants`, { headers: { Authorization: `Bearer ${boot}` }, data: { slug: `e2e-${Date.now().toString(36)}`, name: 'E2E University' } });
  const tenantId = (await t.json()).id as string;
  const admin = await mint(request, tenantId, ['person:create', 'person:read', 'org:create', 'org:read', 'workflow:manage', 'correspondence:read']);
  const H = { Authorization: `Bearer ${admin}` };
  const person = async (fullName: string, departmentId?: string) => (await (await request.post(`${API}/api/people`, { headers: H, data: { tenantId, type: 'Employee', fullName, departmentId } })).json()).id as string;

  // A department with a head, an author in it, a head of the receiving unit, and the standard types and workflows.
  const head = await person('Hana Head');
  const receiver = await person('Rami Receiver');
  const unit = async (code: string, leaderPersonId: string) => (await (await request.post(`${API}/api/organizational-units`, { headers: H, data: { tenantId, code, name: `Unit ${code}`, leaderPersonId } })).json()).id as string;
  const dept = await unit('DEP', head);
  await unit('TGT', receiver);
  const author = await person('Karim Author', dept);
  expect((await request.post(`${API}/api/correspondence/types/seed-defaults`, { headers: H, data: { tenantId } })).ok()).toBeTruthy();

  const token = await mint(request, tenantId, ['correspondence:create', 'correspondence:read'], author);
  await page.goto('/signin');
  await page.evaluate(([tk, tn]) => { localStorage.setItem('edunexus.token', tk); localStorage.setItem('edunexus.tenant', tn); }, [token, tenantId]);
  await page.goto('/correspondence');

  await page.getByRole('button', { name: '+ New letter' }).click();
  await page.getByLabel('Letter type').selectOption({ label: 'Between departments' });
  await page.getByLabel('Receiving unit').selectOption({ label: 'Unit TGT (TGT)' });
  await page.getByLabel('Subject', { exact: true }).fill('Shared lab hours');
  await page.getByLabel('Content', { exact: true }).fill('Please agree the hours.');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  await expect(page.getByText('Head of sending department')).toHaveCount(0);   // the path names people, not step codes
  await expect(page.getByRole('cell', { name: 'Hana Head' })).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Waiting' })).toBeVisible();

  // The head sends it back with a note.
  const headToken = await mint(request, tenantId, ['approval:read', 'approval:decide'], head);
  const approvals = await (await request.get(`${API}/api/approvals?tenantId=${tenantId}&assigneeId=${head}&status=Pending`, { headers: { Authorization: `Bearer ${headToken}` } })).json();
  expect(approvals).toHaveLength(1);
  const back = await request.post(`${API}/api/approvals/${approvals[0].id}/request-changes`, { headers: { Authorization: `Bearer ${headToken}` }, data: { tenantId, comment: 'Add the room numbers' } });
  expect(back.ok()).toBeTruthy();

  await page.reload();
  await page.locator('tbody tr', { hasText: 'Shared lab hours' }).click();
  await expect(page.getByText('Returned for changes. Read the reviewer')).toBeVisible();
  await expect(page.getByRole('cell', { name: 'Add the room numbers' })).toBeVisible();

  // Edit, send again from the first step, then withdraw with a reason.
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await page.getByLabel('Content', { exact: true }).fill('Please agree the hours. Rooms 101 and 102.');
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await page.getByRole('button', { name: 'Send again' }).click();
  await expect(page.locator('tbody tr', { hasText: 'Shared lab hours' })).toContainText('Submitted');
  await page.getByRole('button', { name: 'Withdraw' }).click();
  await page.getByLabel('Reason (required)').fill('Sent by mistake');
  await page.getByRole('button', { name: 'Confirm' }).click();
  await expect(page.locator('tbody tr', { hasText: 'Shared lab hours' })).toContainText('Closed');
  await page.getByText('History', { exact: true }).click();
  await expect(page.getByText(/Withdrawn or closed/)).toBeVisible();
});

test('a student sees only their own letters, and a refusal from the server is explained on screen', async ({ page, request }) => {
  const boot = await mint(request, crypto.randomUUID(), ['tenant:create']);
  const t = await request.post(`${API}/api/tenants`, { headers: { Authorization: `Bearer ${boot}` }, data: { slug: `e2e-${Date.now().toString(36)}`, name: 'E2E University' } });
  const tenantId = (await t.json()).id as string;
  const admin = await mint(request, tenantId, ['person:create', 'workflow:manage', 'correspondence:read']);
  const H = { Authorization: `Bearer ${admin}` };
  const sara = (await (await request.post(`${API}/api/people`, { headers: H, data: { tenantId, type: 'Student', fullName: 'Sara Student' } })).json()).id as string;
  expect((await request.post(`${API}/api/correspondence/types/seed-defaults`, { headers: H, data: { tenantId } })).ok()).toBeTruthy();
  const types = await (await request.get(`${API}/api/correspondence/types?tenantId=${tenantId}&includeInactive=true`, { headers: H })).json();
  const studentType = types.find((x: { code: string }) => x.code === 'STUDENT_REQUEST');
  expect((await request.patch(`${API}/api/correspondence/types/${studentType.id}`, { headers: H, data: { tenantId, isActive: true } })).ok()).toBeTruthy();

  const token = await mint(request, tenantId, ['correspondence:student-request'], sara);
  await page.goto('/signin');
  await page.evaluate(([tk, tn]) => { localStorage.setItem('edunexus.token', tk); localStorage.setItem('edunexus.tenant', tn); }, [token, tenantId]);
  await page.goto('/correspondence');
  await expect(page.getByRole('heading', { name: 'My letters' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Register' })).toHaveCount(0);

  await page.getByRole('button', { name: '+ New letter' }).click();
  await expect(page.getByLabel('Letter type')).toHaveValue('STUDENT_REQUEST');
  await page.getByLabel('Subject', { exact: true }).fill('Enrolment statement');
  await page.getByLabel('Content', { exact: true }).fill('Please issue a statement.');
  await page.getByRole('button', { name: 'Save draft' }).click();
  await page.getByRole('button', { name: 'Send', exact: true }).click();
  // No one holds the student-affairs role in this institution: the student is told what is missing, and the letter stays a draft.
  await expect(page.getByRole('alert').filter({ hasText: 'STUDENT_AFFAIRS' })).toBeVisible();
  await page.reload();
  await expect(page.locator('tbody tr', { hasText: 'Enrolment statement' })).toContainText('Draft');
});
