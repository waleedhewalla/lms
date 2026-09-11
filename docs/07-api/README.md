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

## R2 implemented
- `GET /api/correspondence?tenantId=&status=` (`correspondence:read`; confidential filtered without `correspondence:confidential`)
- `POST /api/correspondence` (`correspondence:create`; reserves `CORR-yyyy-nnnnnn`)
- `POST /api/correspondence/{id}/submit` (Draft→Submitted + approval + task + event)
- `GET /api/approvals?tenantId=&assigneeId=&status=` (`approval:read`)
- `POST /api/approvals/{id}/decide` (`approval:decide`, assignee-only, 409 double-decide)
- `GET /api/tasks?tenantId=&assigneeId=` (`task:read`) / `POST /api/tasks/{id}/complete` (`task:update`)
- `GET /api/sla/breaches?tenantId=` (`approval:read`)
- `GET /api/notifications?tenantId=&personId=` (`notification:read`; written by `NotificationConsumer`)

Enums serialize as strings (JsonStringEnumConverter).

## R3 implemented
- `GET/POST /api/committees` (`committee:read/create`), `POST /api/committees/{id}/members`
- `GET/POST /api/meetings` (`meeting:read/create`), `POST /api/meetings/{id}/attendance|conclude`
- `POST /api/meetings/{id}/decisions` (`decision:create`) → `DecisionPublished`
- `GET /api/decisions` (`decision:read`), `POST /api/decisions/{id}/actions` → `ActionAssigned`
- `POST /api/decision-actions/{id}/advance` (`action:update`) → `ActionAdvanced`
- `GET/POST /api/policies` (`policy:read/create`), `POST /api/policies/{id}/publish|acknowledge` (`policy:ack`), `GET /api/policies/{id}/pending`

## R4 implemented
- `GET/POST /api/documents` (`document:read/create`), `POST /api/documents/{id}/upload-url|/versions`, `GET /api/documents/{id}/download-url` (MinIO presigned, bucket `edunexus-docs`)
- `GET /api/search?q=&tenantId=` (`search:read`, trigram indexes)
- `GET /api/analytics/overview?tenantId=` (`analytics:read`)
- `POST /api/quality/standards|criteria|evidence|findings|corrective-actions` (`quality:manage`)
- `POST /api/strategy/plans|objectives|kpis`, `POST /api/strategy/kpis/{id}/reading` (`strategy:manage`)

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
| CorrespondenceCreated | POST /api/correspondence | tenantId, correspondenceId, number | audit projector |
| CorrespondenceSubmitted | POST /api/correspondence/{id}/submit | tenantId, correspondenceId, number, reviewerId, approvalId | notifications |
| ApprovalDecided | POST /api/approvals/{id}/decide | tenantId, approvalId, entityType, entityId, approved, decidedBy | notifications |
| TaskCompleted | POST /api/tasks/{id}/complete | tenantId, taskId | analytics |
| TaskBreached | SlaMonitor (1 min tick) | tenantId, taskId, title, assigneeId | notifications |
| CommitteeCreated / CommitteeMemberAdded | POST /api/committees[/{id}/members] | tenantId, committeeId, personId | audit projector |
| MeetingScheduled / MeetingConcluded | POST /api/meetings[/{id}/conclude] | tenantId, meetingId, committeeId, title | notifications (members) |
| DecisionPublished | POST /api/meetings/{id}/decisions | tenantId, decisionId, meetingId, committeeId, text | notifications (members) |
| ActionAssigned / ActionAdvanced | POST /api/decisions/{id}/actions[/advance] | tenantId, actionId, assigneeId, description | notifications (assignee) |
| PolicyCreated / PolicyPublished / PolicyAcknowledged | POST /api/policies[/{id}/publish|/acknowledge] | tenantId, policyId, code | notifications (all staff on publish) |
| DocumentCreated / DocumentVersionAdded | POST /api/documents[/{id}/versions] | tenantId, documentId, version | search indexer (R5) |
| EvidenceAdded / FindingOpened / CorrectiveActionOpened | POST /api/quality/… | tenantId, ids | audit projector |
| KpiUpdated | POST /api/strategy/kpis/{id}/reading | tenantId, kpiId, current, target | analytics |

`OutboxEvents` is intentionally **exempt from RLS** (platform-level, like `Tenants`)
so the relay reads all tenants; isolation is enforced by consumer-side `tenant_id` filtering.

## Integrations
SIS/HR/Finance/ERP bi-di API+events; Teams Graph; Email SMTP/API in+out; SMS out; Identity OIDC/SAML/LDAP in; Zoom/Workspace bi-di. Connectors in R5.
