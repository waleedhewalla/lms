# ADR 0002: DocumentActionRule Execution Model — Inline vs. Outbox Queue

**Date:** 2026-09-20  
**Status:** Accepted  
**Deciders:** Architecture Guild, Lead Backend Engineer  

---

## Context and Problem Statement

When a user tags a document (`POST /api/documents/{id}/tags`), automated action rules (`DocumentActionRule`) are triggered (e.g. `AutoRetention` or `ScheduleReviewActivity`). We needed to decide whether rule execution should run **inline within the HTTP transaction** or **asynchronously via the Outbox -> RabbitMQ worker queue**.

---

## Decision Drivers

1. **Transaction Integrity**: `AutoRetention` and `ScheduleReviewActivity` mutate PostgreSQL database state (`DocumentRetentionPolicy`, `ScheduledActivity`).
2. **Latency & UX**: Tagging a document is a quick user action. Heavy background jobs must not block the HTTP thread.
3. **Fault Tolerance**: If rule execution fails (e.g., malformed JSON payload), the primary tag creation operation must succeed while logging an explicit audit event.

---

## Decision Outcome

**Chosen Option:** **Inline Execution with Outbox Audit Fallback** for Phase 1, with an optional Feature Flag for Outbox Worker dispatch in Phase 2.

### Rationale:
- Current action types (`AutoRetention` and `ScheduleReviewActivity`) only perform fast in-memory JSON parsing and local PostgreSQL inserts within the open `TenantScope` transaction.
- Running inline guarantees immediate consistency: when the tag response returns, the retention policy or review task is already saved and queryable.
- Non-fatal error handling was added (BUG-01 fix): JSON parsing or configuration errors record a `DocumentActionRuleFailed` outbox event to `AuditEvents` without failing the primary HTTP request.

---

## Consequences

### Positive:
- Zero queue latency; immediate read-your-writes consistency for retention policies and scheduled activities.
- Simple architecture with no external worker process required for basic rule execution.
- Failures are fully observable in the `AuditEvents` log stream.

### Negative / Risks:
- Future complex action types (e.g., third-party webhooks or heavy AI indexing) MUST NOT run inline and should be routed through the `EventRelay` / `IntegrationDispatcher` background worker.
