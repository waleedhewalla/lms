import { apiBase } from "./config";

/** Every permission, for development sign-in only. The API refuses /api/auth/dev-token outside Development. */
export const DEV_PERMISSIONS: string[] = ["tenant:read", "org:read", "person:read", "person:create", "role:create", "role:assign", "role:read", "audit:read", "correspondence:create", "correspondence:read", "correspondence:confidential", "approval:read", "approval:decide", "task:read", "task:update", "notification:read", "committee:create", "committee:read", "meeting:create", "meeting:read", "decision:create", "decision:read", "action:update", "policy:create", "policy:read", "policy:ack", "document:create", "document:read", "search:read", "analytics:read", "quality:manage", "strategy:manage", "ai:manage", "ai:ask", "ai:read", "integration:manage", "integration:read", "inbox:read", "quality:read", "strategy:read", "communication:create", "notification:manage", "form:manage", "request:create", "request:read", "workflow:manage", "workflow:read", "document:update", "document:manage", "chatter:read", "chatter:write", "activity:read", "activity:write", "task:create", "task:verify", "person:update", "communication:read", "form:read", "calendar:read", "calendar:manage", "minutes:approve", "action:verify", "sla:manage", "records:read", "records:manage", "records:approve", "records:hold", "privacy:manage", "meeting:update", "analytics:read"];

async function post<T>(path: string, body: unknown, token?: string): Promise<T> {
  const res = await fetch(`${apiBase()}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: JSON.stringify(body),
  });
  if (!res.ok) throw new Error(`${path} → ${res.status}${res.status === 404 ? " (the API must run in Development)" : ""}`);
  return res.json() as Promise<T>;
}

/** A development token for a tenant (and optionally acting as a person), with every permission. */
export async function mintDevToken(tenantId: string, personId?: string | null, subject = "web-admin"): Promise<string> {
  const r = await post<{ token: string }>("/api/auth/dev-token", { subject, tenantId, personId: personId?.trim() || null, permissions: DEV_PERMISSIONS });
  return r.token;
}

/** Creates a demo institution to try the product locally; returns its id. */
export async function createDemoTenant(): Promise<string> {
  const boot = (await post<{ token: string }>("/api/auth/dev-token", { subject: "bootstrap", tenantId: crypto.randomUUID(), permissions: ["tenant:create"] })).token;
  const slug = `demo-${Date.now().toString(36)}`;
  const t = await post<{ id: string }>("/api/tenants", { slug, name: "Demo University" }, boot);
  return t.id;
}
