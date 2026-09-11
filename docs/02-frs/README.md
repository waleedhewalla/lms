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

## Release 2 FRs

### FR-COR-001 — Create Correspondence ✅ implemented
Actor: authorized employee with `correspondence:create`. Preconditions: authenticated,
tenant active, author exists. Behavior: 1) select type 2) form generated 3–8) recipient/
subject/content/attachments-meta/priority/confidentiality 9) submit draft 10) validate
11) number reserved at creation `CORR-yyyy-nnnnnn` (unique per tenant, atomic sequence)
12) submit starts approval workflow 13) audit + outbox 14) reviewer notified (broker→consumer→in-app).
Rules: confidential needs `correspondence:confidential` (create + list filtering);
High/Urgent → Accelerated SLA (due +2d, else +5d); submit only from Draft (409 otherwise).
ACs covered by `Correspondence_FullFlow_SubmitDecide_Notifies` + `Correspondence_Confidential_Gated`.

### FR-APR-001 — Decide Approval ✅ implemented
Actor: assignee with `approval:decide` (assignee match enforced, 403 otherwise).
Behavior: Pending→Approved/Rejected + comment; linked correspondence status follows;
linked task marked Done; `ApprovalDecided` event. Double-decide → 409.

### FR-TSK-001 — Complete Task ✅ / FR-SLA-001 — Breach Detection ✅
Tasks auto-created on submit; `SlaMonitor` marks overdue Open/InProgress tasks Breached
+ `TaskBreached` event; breaches queryable at `GET /api/sla/breaches`.
Actor: authorized employee. Behavior: validate tenant → create Employee/Student → `PersonCreated` audit. PG migration pending (see roadmap).

Full FRS target 150–250 pages; add FR-COR-001 etc. in Release 2.
