# 07 — API & Integration Specification

Per endpoint: method, auth, authz, request/response, validation, errors, paging/filter/sort, rate limits, audit, events.

## Catalogue
`/auth /users /organizations /people /communications /correspondence /requests /cases /forms /workflows /approvals /tasks /slas /meetings /committees /decisions /policies /documents /notifications /calendar /resources /quality /accreditation /strategy /analytics /ai /integrations /audit`

## R1 implemented — all Postgres-backed with RLS (see `backend/src/EduNexus.Api`)
- `GET /health` — no auth
- `GET /api/tenants` (`tenant:read`) / `POST /api/tenants` (`tenant:create`, 409 duplicate slug)
- `GET /api/organizations?tenantId=` (`org:read`) / `POST /api/organizations` (`org:create`, 404/409/403)
- `GET /api/people?tenantId=&q=` (`person:read`) / `POST /api/people` (`person:create`)
- `GET /api/audit?tenantId=&limit=` (`audit:read`, max 1000)
- `POST /api/auth/dev-token` — Development only; mints HS256 JWT (`sub`, `tenant_id`, `permission[]`)
- `GET /api/roles?tenantId=` — auth + `role:read`
- `POST /api/roles` — auth + `role:create`, 409 on duplicate code
- `POST /api/roles/assign` — auth + `role:assign`, SoD → 409; writes `RoleAssigned` audit
- `POST /api/roles/revoke` — auth + `role:assign`, 204; writes `RoleRevoked` audit
- `GET /api/roles/assignments?tenantId=&personId=` — auth + `role:read`

Auth: OIDC (set `Auth:Authority`) or dev HS256. Every mutation runs in a tenant-scoped
tx (`TenantScope`, LOCAL GUC) and writes `AuditEvent` + `OutboxEvent` atomically
(`DomainEvents.Record`). OpenAPI at `/openapi/v1.json` (dev).

## Events (R1 live via outbox → RabbitMQ)
Exchange `edunexus.events` (topic, durable); routing key = event type; `tenant_id` header;
persistent delivery; relay `EventRelay` (2s poll, batch 50, marks `DispatchedAt` post-publish).

| Event | Producer endpoint | Payload fields | Consumers (planned) |
|---|---|---|---|
| TenantCreated | POST /api/tenants | tenantId, slug | audit projector, notifications |
| OrganizationCreated | POST /api/organizations | tenantId, org id, code | audit projector |
| PersonCreated | POST /api/people | tenantId, person id | directory sync, notifications |
| RoleCreated | POST /api/roles | tenantId, roleId, code, permissions | audit projector |
| RoleAssigned | POST /api/roles/assign | tenantId, personId, roleId, roleCode, scope | notifications, access cache |
| RoleRevoked | POST /api/roles/revoke | tenantId, personId, roleCode | notifications, access cache |

`OutboxEvents` is intentionally **exempt from RLS** (platform-level, like `Tenants`)
so the relay reads all tenants; isolation is enforced by consumer-side `tenant_id` filtering.

## Integrations
SIS/HR/Finance/ERP bi-di API+events; Teams Graph; Email SMTP/API in+out; SMS out; Identity OIDC/SAML/LDAP in; Zoom/Workspace bi-di. Connectors in R5.
