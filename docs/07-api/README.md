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
- `POST /api/approvals/{id}/request-changes` (assignee-only → ChangesRequested, request returned for revision)
- `POST /api/approvals/{id}/delegate` (current assignee only; moves approval + task, notifies)
- `PATCH /api/tasks/{id}` (title/description/priority/progress/status), `GET /api/tasks/{id}` detail
- `POST/GET /api/tasks/{id}/comments|/evidence` (discussion + object-store evidence links)
- `POST/GET /api/notification-templates` (`notification:manage`, unique code+channel, `{token}` rendering)
- `GET /api/notification-receipts` (`notification:read`, per-channel delivery log)
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

## R0.1 Track A implemented
- `GET/POST /api/forms` (`form:read` / `form:manage`, unique code), `POST /api/forms/{id}/validate`
- `GET /api/requests?tenantId=&status=&category=` (`request:read`)
- `POST /api/requests` (`request:create`, validated submission snapshot, `REQ-yyyy-nnnnnn`)
- `POST /api/requests/{id}/submit` (Draft-only → Submitted + optional reviewer approval/task, `RequestSubmitted`)

## R0.1 Track B implemented
- `POST /api/workflows/definitions` (`workflow:manage`, validated node graph), `GET /api/workflows/definitions`, `GET /api/workflows/instances?status=`
- `POST /api/requests/{id}/submit` with `workflowCode` → `WorkflowInstance` (Running) + chained approvals/tasks via `WorkflowRunner`
- Approvals in a workflow advance the instance (`WorkflowAdvanced`) or mark it `Rejected` on any reject

## R0.1 Tracks C+D implemented
- `POST/GET /api/communications` (`communication:create/read`), `POST /api/communications/{id}/publish` (Draft→Published, Directive fans out tasks)
- `GET /api/inbox?tenantId=&personId=&filter=` (`inbox:read`, aggregation across approvals/tasks/communications/requests/notifications with BBP filters)
- `GET /api/my-work?tenantId=&personId=` (`inbox:read`, counters + priorityWork + recentCommunications for My Work dashboard)

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
| ApprovalChangesRequested / ApprovalDelegated | POST /api/approvals/{id}/request-changes\|/delegate | tenantId, approvalId, decidedBy | requester, new assignee |
| TaskUpdated / TaskCommentAdded / TaskEvidenceAdded | PATCH/POST /api/tasks/{id}… | tenantId, taskId | analytics, inbox |
| TaskEscalated | SlaMonitor (reminder >48h, escalation >72h) | tenantId, taskId, level, from/to | assignee, escalation owner |
| CommitteeCreated / CommitteeMemberAdded | POST /api/committees[/{id}/members] | tenantId, committeeId, personId | audit projector |
| MeetingScheduled / MeetingConcluded | POST /api/meetings[/{id}/conclude] | tenantId, meetingId, committeeId, title | notifications (members) |
| DecisionPublished | POST /api/meetings/{id}/decisions | tenantId, decisionId, meetingId, committeeId, text | notifications (members) |
| ActionAssigned / ActionAdvanced | POST /api/decisions/{id}/actions[/advance] | tenantId, actionId, assigneeId, description | notifications (assignee) |
| PolicyCreated / PolicyPublished / PolicyAcknowledged | POST /api/policies[/{id}/publish|/acknowledge] | tenantId, policyId, code | notifications (all staff on publish) |
| DocumentCreated / DocumentVersionAdded | POST /api/documents[/{id}/versions] | tenantId, documentId, version | search indexer (R5) |
| EvidenceAdded / FindingOpened / CorrectiveActionOpened | POST /api/quality/… | tenantId, ids | audit projector |
| KpiUpdated | POST /api/strategy/kpis/{id}/reading | tenantId, kpiId, current, target | analytics |
| CommunicationCreated / CommunicationPublished | POST /api/communications[/{id}/publish] | tenantId, communicationId, kind | notifications, tasks (directive), audit |
| DirectiveTasksCreated | POST /api/communications/{id}/publish (directive) | tenantId, communicationId, tasks | inbox, my-work |

`OutboxEvents` is intentionally **exempt from RLS** (platform-level, like `Tenants`)
so the relay reads all tenants; isolation is enforced by consumer-side `tenant_id` filtering.

## Integrations
SIS/HR/Finance/ERP bi-di API+events; Teams Graph; Email SMTP/API in+out; SMS out; Identity OIDC/SAML/LDAP in; Zoom/Workspace bi-di. Connectors in R5.

## Permission enforcement (every route checks a code, not just the tenant)
- Forms/Requests/Workflows: `form:manage`; `request:create` (create, submit) / `request:read`; `workflow:manage` / `workflow:read`
- Documents: workspaces, shares, tags, retention, action rules read with `document:read`; writes need `document:update|manage|write`
- Quality / Strategy: `quality:read|manage`, `strategy:read|manage`
- Search `search:read` · Analytics `analytics:read` · AI ask `ai:ask`, interactions `ai:read` · Integration deliveries `integration:read`
- Communications `communication:create` (create, publish) · Notification templates `notification:manage` · receipts `notification:read`
- Approvals: detail `approval:read`; request-changes/delegate `approval:decide` · SLA breaches `approval:read`
- Tasks: detail/comments/evidence `task:read`; complete/patch/comment/evidence `task:update` · Inbox and My Work `inbox:read`
- Chatter: comments/followers `chatter:read`, comment/follow `chatter:write` · Activities: `/my` `activity:read`, schedule/complete `activity:write`

## Acting person (who decides)
Approval decide, request-changes and delegate act as the **token's person**, resolved server-side:
the `person_id` claim, else the single person in the tenant whose email equals the `email` claim.
A token with no resolvable person gets 403. A body `decidedBy`/`delegatedBy` that names anyone
else is refused with 403. The approval must be assigned to that person and still Pending (else 409).
Delegation needs a deputy in the same tenant other than yourself. Dev tokens accept an optional `personId`.

## R0.1 read & detail endpoints (BBP §7.2, no schema change)
- `GET /api/people/{id}` (`person:read`) · `GET /api/organizational-units/{id}/subtree` (`org:read`, unit + all descendants)
- `GET /api/correspondence/{id}` (`correspondence:read`; confidential needs `correspondence:confidential`, else 404) — with correspondents and approvals
- `GET /api/communications?kind=&status=`, `GET /api/communications/{id}` (+ recipientCount), `GET /api/communications/{id}/recipients` (`communication:read`)
- `GET /api/requests/categories`, `GET /api/requests/{id}` (+ submissions, approvals, workflow) (`request:read`)
- `POST /api/requests/{id}/cancel` (`request:create`; acting person must be the submitter; Draft/ChangesRequested → Closed; `RequestCancelled`)
- `GET /api/forms?category=`, `GET /api/forms/{id}`, `POST /api/forms/{id}/validate` → `{ valid, errors[] }` (`form:read`)
- `GET /api/workflows/definitions`, `GET /api/workflows/definitions/{id}`, `GET /api/workflows/instances/{id}` (+ approvals) (`workflow:read`)
- `GET /api/approvals/{id}/history` (`approval:read`) · `GET /api/audit/entities/{type}/{id}` (`audit:read`)
- `POST /api/notifications/{id}/read` (`notification:read`; only the recipient, via the token's person)

## R0.1 wave A (BBP §7.2, no schema change)
- `GET /api/auth/me` (subject, tenant, permissions, resolved person) · `GET /api/permissions` (`role:read`, full catalog)
- `PATCH /api/people/{id}` (`person:update`) · `GET /api/people/{id}/direct-reports` (members of units the person leads)
- `POST /api/tasks` (`task:create`, manual task) · `POST /api/tasks/{id}/verify` (`task:verify`; Done → Verified; never by the assignee)
- `GET /api/approvals/submitted` (`approval:read`; approvals on requests/correspondence the caller submitted)
- `GET /api/inbox/counts` (`inbox:read`; approvals, open/overdue tasks, unread notifications, activities for the caller)
- `GET /api/sla/compliance?from=&to=` (`approval:read`; on-time vs late decisions, pending overdue, breached tasks)
- `POST /api/communications/{id}/archive` (`communication:create`; Published/Draft → Archived)
