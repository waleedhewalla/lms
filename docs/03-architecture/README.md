# 03 — Technical & Solution Architecture

## Context / logical / physical / deployment / domain
- Modular monolith first (Foundation, Communication, Workflow, Governance), extract to microservices at R4–R5 boundaries.
- Modules: `EduNexus.Foundation` (R1) → `Communication`, `Workflow`, `Governance`, …

## Technology decisions (why, not just what)
| Layer | Choice | Why |
|-------|--------|-----|
| Web | Next.js+TS | SSR, i18n/RTL, PWA path |
| Backend | ASP.NET Core .NET 9 | DI, OpenAPI, OIDC, EF Core, OTel |
| DB | PostgreSQL | JSONB, RLS for multi-tenancy, pg_trgm |
| Cache | Redis | Sessions, idempotency, SLA timers |
| Search | OpenSearch | Arabic analyzers, RAG index |
| Queue | RabbitMQ | Delayed SLA/escalation, outbox relay |
| Objects | S3-compat (MinIO) | Versions, retention |
| Auth | OIDC/OAuth2 | External IdP, SAML/LDAP bridge |
| Deploy | Docker/K8s | Compose for dev, K8s for prod |
| Obs | OTel+Prom+Grafana | Traces→logs→metrics correlation |

## Cross-cutting
Multi-tenancy: `tenant_id` on every row + RLS. Outbox → RabbitMQ events (`ApprovalCompleted`…). OTel everywhere. DR: PITR + cross-region object replication (RPO/RTO in NFRs).

ADRs: `docs/03-architecture/adr/` — record each significant choice.
