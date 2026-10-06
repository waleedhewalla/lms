import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '15s', target: 10 }, // ramp
    { duration: '60s', target: 10 }, // sustain
    { duration: '15s', target: 0 },  // down
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    checks: ['rate>0.99'],
    http_req_duration: ['p(95)<200'],
  },
};

const BASE = __ENV.BASE_URL || 'http://127.0.0.1:5000';
const PERMS = [
  'tenant:create', 'tenant:read', 'org:read', 'person:create', 'person:read',
  'correspondence:create', 'correspondence:read', 'meeting:create', 'meeting:read', 'meeting:update',
  'document:create', 'document:read', 'document:update', 'document:write', 'audit:read',
  'committee:create', 'committee:read', 'activity:read', 'activity:write',
  'inbox:read', 'analytics:read', 'approval:read', 'task:read', 'request:read', 'communication:read'
];

export function setup() {
  const tenantId = '11111111-1111-1111-1111-111111111111';
  const h = { 'Content-Type': 'application/json' };
  
  let r = http.get(`${BASE}/health`);
  check(r, { 'health 200': (x) => x.status === 200 });
  
  r = http.post(`${BASE}/api/auth/dev-token`, JSON.stringify({ subject: 'k6-write-mix', tenantId, permissions: PERMS }), { headers: h });
  check(r, { 'token 200': (x) => x.status === 200 });
  const token = r.json('token');
  const auth = { headers: { ...h, Authorization: `Bearer ${token}` } };
  
  const slug = `k6-wm-${Date.now()}`;
  r = http.post(`${BASE}/api/tenants`, JSON.stringify({ slug, name: 'k6 Write-Mix Tenant' }), auth);
  check(r, { 'seed tenant 201': (x) => x.status === 201 });
  const tid = r.json('id');
  
  // Mint tenant-scoped token
  r = http.post(`${BASE}/api/auth/dev-token`, JSON.stringify({ subject: 'k6-write-mix', tenantId: tid, permissions: PERMS}), { headers: h });
  const tenantToken = r.json('token');
  const tenantAuth = { headers: { ...h, Authorization: `Bearer ${tenantToken}` } };

  // Create seed author/person
  r = http.post(`${BASE}/api/people`, JSON.stringify({ tenantId: tid, type: 'Employee', fullName: 'k6 Tester', email: 'k6@edunexus.edu' }), tenantAuth);
  const authorId = r.json('id');

  // Create seed document
  r = http.post(`${BASE}/api/documents`, JSON.stringify({ tenantId: tid, title: 'k6 Baseline Document' }), tenantAuth);
  const docId = r.json('id');

  // Create seed committee & meeting
  r = http.post(`${BASE}/api/committees`, JSON.stringify({ tenantId: tid, code: `COM-${Date.now()}`, name: 'k6 Governance Committee' }), tenantAuth);
  const committeeId = r.json('id');

  r = http.post(`${BASE}/api/meetings`, JSON.stringify({ tenantId: tid, committeeId, title: 'k6 Board Meeting', startsAt: new Date().toISOString(), agenda: [{ title: 'Approve Budget' }] }), tenantAuth);
  const meetingId = r.json('id');

  // Get agenda item ID
  r = http.get(`${BASE}/api/meetings/${meetingId}/packet?tenantId=${tid}`, tenantAuth);
  const agendaItemId = r.json('agenda.0.id');

  // Re-mint acting as the seeded person so person-scoped reads (inbox counts) resolve.
  r = http.post(`${BASE}/api/auth/dev-token`, JSON.stringify({ subject: 'k6-write-mix', tenantId: tid, permissions: PERMS, personId: authorId }), { headers: h });
  const personToken = r.json('token');
  for (const [name, id] of Object.entries({ authorId, docId, committeeId, meetingId, agendaItemId }))
    if (!id) throw new Error(`setup: could not seed ${name}`);
  return { token: personToken || tenantToken, tenantId: tid, authorId, docId, meetingId, agendaItemId };
}

export default function (data) {
  const auth = { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${data.token}` } };
  const t = data.tenantId;
  const pick = Math.random();
  let r;

  if (pick < 0.20) {
    r = http.get(`${BASE}/api/inbox/counts?tenantId=${t}`, auth);
    check(r, { 'inbox counts 200': (x) => x.status === 200 });
  } else if (pick < 0.40) {
    r = http.get(`${BASE}/api/dashboards/executive?tenantId=${t}`, auth);
    check(r, { 'executive dashboard 200': (x) => x.status === 200 });
  } else if (pick < 0.55) {
    // Write 1: Create Correspondence
    r = http.post(`${BASE}/api/correspondence`, JSON.stringify({
      tenantId: t, type: 'Internal', subject: `Memo VU-${__VU}-${__ITER}`, content: 'k6 load test memo content', authorId: data.authorId
    }), auth);
    check(r, { 'correspondence create 201': (x) => x.status === 201 });
    if (r.status !== 201 && __ITER < 2) console.warn(`correspondence ${r.status} ${r.body}`);
  } else if (pick < 0.70) {
    // Write 2: Add Document Tag (triggers action rules)
    r = http.post(`${BASE}/api/documents/${data.docId}/tags`, JSON.stringify({
      tenantId: t, category: 'ISO', value: 'ISO-9001'
    }), auth);
    check(r, { 'document tag 201': (x) => x.status === 201 });
    if (r.status !== 201 && __ITER < 2) console.warn(`tag ${r.status} ${r.body}`);
  } else if (pick < 0.85) {
    // Write 3: Cast Meeting Vote
    if (data.agendaItemId) {
      r = http.post(`${BASE}/api/meetings/${data.meetingId}/votes`, JSON.stringify({
        tenantId: t, agendaItemId: data.agendaItemId, personId: data.authorId, choice: 'InFavor', remarks: 'k6 vote'
      }), { ...auth, responseCallback: http.expectedStatuses(200, 409) });
      // One vote per person per item: the first succeeds, repeats are a clean 409 (expected under load).
      check(r, { 'meeting vote 200/409': (x) => x.status === 200 || x.status === 409 });
    }
  } else {
    // Write 4: Schedule Activity
    r = http.post(`${BASE}/api/activities`, JSON.stringify({
      tenantId: t, entityType: 'Document', entityId: data.docId, type: 'Review', assigneeId: data.authorId, summary: `Review VU-${__VU}`, dueDate: new Date().toISOString()
    }), auth);
    check(r, { 'activity schedule 201': (x) => x.status === 201 });
    if (r.status !== 201 && __ITER < 2) console.warn(`activity ${r.status} ${r.body}`);
  }

  sleep(0.2);
}
