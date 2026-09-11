import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 10 }, // ramp
    { duration: '60s', target: 10 }, // sustain
    { duration: '15s', target: 0 },  // down
  ],
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
  },
};

const BASE = __ENV.BASE_URL || 'http://127.0.0.1:5299';
const PERMS = ['tenant:create', 'tenant:read', 'org:read', 'person:read', 'role:create', 'role:assign', 'role:read', 'audit:read'];

export function setup() {
  const tenantId = '11111111-1111-1111-1111-111111111111';
  const h = { 'Content-Type': 'application/json' };
  let r = http.get(`${BASE}/health`);
  check(r, { 'health 200': (x) => x.status === 200 });
  r = http.post(`${BASE}/api/auth/dev-token`, JSON.stringify({ subject: 'k6', tenantId, permissions: PERMS }), { headers: h });
  check(r, { 'token 200': (x) => x.status === 200 });
  const token = r.json('token');
  const auth = { headers: { ...h, Authorization: `Bearer ${token}` } };
  // warmup: JWT validation + EF model + PG pool before the first write
  r = http.get(`${BASE}/api/tenants`, auth);
  check(r, { 'warmup tenants 200': (x) => x.status === 200 });
  // unique slug per run: seed must always be 201 (k6 v2 counts 4xx in http_req_failed)
  const slug = `k6-uni-${Date.now()}`;
  r = http.post(`${BASE}/api/tenants`, JSON.stringify({ slug, name: 'k6 University' }), auth);
  check(r, { 'seed tenant 201': (x) => x.status === 201 });
  const tid = r.json('id');
  // scoped token: tenant claim must equal the tenant row id
  r = http.post(`${BASE}/api/auth/dev-token`, JSON.stringify({ subject: 'k6', tenantId: tid, permissions: PERMS }), { headers: h });
  return { token: r.json('token'), tenantId: tid };
}

export default function (data) {
  const auth = { headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${data.token}` } };
  const t = data.tenantId;
  const pick = Math.random();
  let r;
  if (pick < 0.4) {
    r = http.get(`${BASE}/api/roles?tenantId=${t}`, auth); // RLS read
    check(r, { 'roles 200': (x) => x.status === 200 });
  } else if (pick < 0.7) {
    r = http.get(`${BASE}/api/people?tenantId=${t}`, auth); // directory read
    check(r, { 'people 200': (x) => x.status === 200 });
  } else if (pick < 0.9) {
    r = http.get(`${BASE}/api/audit?tenantId=${t}&limit=20`, auth); // audit read
    check(r, { 'audit 200': (x) => x.status === 200 });
  } else {
    const code = `k6-r-${__VU}-${__ITER}`; // unique write per VU/iter
    r = http.post(`${BASE}/api/roles`, JSON.stringify({ tenantId: t, code, name: code, permissions: ['x'] }), auth);
    check(r, { 'role create 201': (x) => x.status === 201 });
  }
  sleep(0.2);
}
