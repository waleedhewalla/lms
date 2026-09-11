# 09 — AI Specification

Domains: AI-01 Communication · AI-02 Correspondence · AI-03 Workflow Copilot · AI-04 Meeting Intelligence · AI-05 Document Intelligence · AI-06 Search/RAG · AI-07 Executive Copilot · AI-08 Analytics Copilot · AI-09 Knowledge Assistant.

Per capability: input/output, model, data sources, permissions, RAG scope, prompt policy, human approval, confidence, explainability, logging (`AIInteraction`), privacy, retention, failure behavior.

Governance (mandatory from R5 design now): tenant-scoped RAG, permission-filtered retrieval, PII redaction, human-in-loop for outbound/executive actions, full `AIInteraction` audit, Arabic+English evals.

## R5 implemented (AI-06 + ask copilot foundation)

- `POST /api/ai/index?tenantId=` (`ai:manage`): builds per-tenant OpenSearch index
  `edunexus-{guid}` (Arabic content analyzer: standard tokenizer + lowercase +
  arabic_normalization) from correspondence/decisions/documents/policies; emits `TenantIndexed`.
- `POST /api/ai/ask` (`ai:ask`): retrieve top-5 (OpenSearch multi_match, PG-trigram
  fallback when the cluster is unreachable) → generate via provider:
  - `Echo` (default, air-gap safe): extractive passage list, no external calls.
  - `OpenAI` (OpenAI-compatible `/chat/completions` via `AI:BaseUrl/ApiKey/Model`):
    system prompt restricts to retrieved passages.
- Every call appends `AiInteraction` (capability, 500-char input excerpt, 2000-char output
  excerpt, model, requester, timestamp); readable at `GET /api/ai/interactions` (`ai:read`).
- Read-only copilot: no autonomous writes → human-approval gate deferred to agentic R6;
  `requestedBy`/`confidence` columns reserved in the log for it.
- Webhooks (ecosystem seed): `POST/GET/DELETE /api/integrations/endpoints`
  (`integration:manage/read`), HMAC-SHA256 signed POSTs (`X-EduNexus-Signature`),
  `GET /api/integrations/deliveries` log; `IntegrationDispatcher` fan-out with requeue.
