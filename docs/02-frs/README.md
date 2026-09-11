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

## Release 3 FRs

### FR-GOV-001 — Committee & Meeting Lifecycle ✅ implemented
Committee CRUD + members (unique per person); meeting schedule with agenda items,
attendance (upsert per person), conclude with minutes. Statuses guard transitions
(double-conclude → 409, decisions on cancelled meetings → 409).

### FR-GOV-002 — Decision Publication & Actions ✅ implemented (BP-ACD-014)
Decision published at creation → `DecisionPublished` (notifies committee members);
actions assigned to people → `ActionAssigned` (notifies assignee); advance
Assigned→InProgress→Done→Verified. Covered by `Governance_FullChain_MeetingToAction_PolicyAck`.

### FR-GOV-003 — Policy Publish & Acknowledge ✅ implemented
Draft→Published (double-publish → 409); per-person acknowledge (double-ack → 409);
`GET /api/policies/{id}/pending` lists active staff without ack (compliance view).

## Release 4 FRs

### FR-DOC-001 — Document Versioning ✅ implemented
Draft → versions (objectKey in S3/MinIO via presigned PUT URL, sha256 recorded) →
optional publish on version add. `GET .../download-url` mints presigned GET.
Covered by `Intelligence_Documents_Search_Analytics_Quality_Strategy`.

### FR-SRH-001 — Unified Search ✅ implemented
`GET /api/search?q=&tenantId=` (min 2 chars) across correspondence/people/decisions/
documents/policies with pg_trgm GIN indexes; confidential correspondence excluded
without `correspondence:confidential`. OpenSearch sync is the R5 scale path.

### FR-ANL-001 — Executive Overview ✅ implemented
`GET /api/analytics/overview` aggregates correspondence-by-status, pending/breached
approvals, open/breached tasks, decisions, open actions, policy ack rate.

### FR-QA-001 — Accreditation Evidence Chain ✅ implemented
Standard → Criterion → Evidence (polymorphic entity link) → Finding (severity) →
CorrectiveAction (assignee + due). Each step validated + audited + evented.

### FR-STR-001 — Strategy & KPIs ✅ implemented
Plan → Objective (unique code) → KPI (target/current/unit) → readings append via
`POST /api/strategy/kpis/{id}/reading` with `KpiUpdated` event.

## Release 5 FRs

### FR-AI-001 — Tenant Indexing ✅ implemented (AI-06)
`POST /api/ai/index?tenantId=` builds a per-tenant OpenSearch index (Arabic analyzer)
over correspondence/decisions/documents/policies. Idempotent (PUT index); emits `TenantIndexed`.

### FR-AI-002 — Ask Copilot ✅ implemented
`POST /api/ai/ask` (perm `ai:ask`): retrieve top-5 (OS, PG fallback) → Echo extractive
or OpenAI-compatible generative answer → mandatory `AiInteraction` log row.
Covered by `Ai_AskEcho_LogsInteraction` (passage grounding + log assertions).

### FR-INT-001 — Outbound Webhooks ✅ implemented
Register per-(tenant, event) HTTPS endpoints (URL validated, secret auto-generated);
`IntegrationDispatcher` POSTs HMAC-signed payloads; `IntegrationDelivery` log records
Delivered/Failed. Covered by `Integrations_Webhook_FailedDelivery_Logged`.
Actor: authorized employee. Behavior: validate tenant → create Employee/Student → `PersonCreated` audit. PG migration pending (see roadmap).

Full FRS target 150–250 pages; add FR-COR-001 etc. in Release 2.
