# 02 — Functional Requirements Specification (FRS)

Developer/PO contract. One FR per behavior. Format:

## Template

**FR-XXX-nnn — Title**
Actor / Preconditions / Functional behavior (numbered) / Business rules / Acceptance criteria (Given/When/Then) / Audit+events

## Release 1 FRs (seed)

### FR-TEN-001 — Create Tenant ✅ implemented (Postgres)
Actor: user with `tenant:create`. Behavior: validate slug → insert tenant → `TenantCreated` audit in tenant-scoped tx → 201.
Rules: slug `^[a-z0-9-]{3,63}$`, unique (409 on conflict); name required.
AC: Given valid payload When POST /api/tenants Then 201 + audit row. Covered by `Tenant_DuplicateSlug_409`.

### FR-ORG-001 — Create Organization ✅ implemented (Postgres)
Actor: user with `org:create`; tenant claim must match. Behavior: tenant must exist → unique (tenant,code) → `OrganizationCreated` audit.
Rules: 404 unknown tenant; 409 duplicate code; 403 cross-tenant.

### FR-AUTH-003 — Revoke Role ✅ implemented (Postgres)
Actor: user with `role:assign`. Behavior: find assignment by (tenant,person,roleCode) → delete → `RoleRevoked` audit → 204; 404 when absent.
AC: Given assigned role When POST /api/roles/revoke Then 204 + approver becomes assignable (SoD cleared).

### FR-AUTH-001 — Assign Role ✅ implemented (R1 hardening)
Actor: user with `role:assign`. Preconditions: authenticated; `tenant_id` claim matches payload; role exists in tenant.
Behavior: 1) open tenant-scoped tx (RLS GUC local) 2) load existing role codes 3) SoD check (`finance.requester` ✕ `finance.approver`) → 409 on conflict 4) create RoleAssignment + `RoleAssigned` audit 5) commit.
AC: Given requester assigned When assigning approver to same person Then 409 + no write. Given unauthenticated When POST /api/roles/assign Then 401.

### FR-AUTH-002 — Create Role ✅ implemented (R1 hardening)
Actor: user with `role:create`. Behavior: validate code unique per tenant → persist permissions (jsonb) → `RoleCreated` audit.
AC: Given duplicate code in tenant When POST /api/roles Then 409.

### FR-DIR-001 — Register Person ✅ implemented (Postgres)
Actor: authorized employee. Behavior: validate tenant → create Employee/Student → `PersonCreated` audit. PG migration pending (see roadmap).

Full FRS target 150–250 pages; add FR-COR-001 etc. in Release 2.
