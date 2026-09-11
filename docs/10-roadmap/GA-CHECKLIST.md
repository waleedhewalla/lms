# GA release gates — v1.1.0 working slice (R1–R5 foundations)

Scope honesty: this tag certifies a **working vertical slice of every release**,
not the full V2 document page-counts nor customer-specific production hardening.
Each gate below is PASS, PARTIAL, or OPEN (v1.0.0 → v1.1.0 deltas noted).

## Gates

| # | Gate | Status | Evidence |
|---|---|---|---|
| 1 | Solution builds 0 errors | PASS | `dotnet build EduNexus.sln` (warnings: NU1902 OTLP, NU1603 Prometheus float) |
| 2 | Unit tests | PASS | 3/3 `Foundation.Tests` |
| 3 | Integration tests (real PG+RMQ+OS+MinIO) | PASS | 13/13 `Api.Tests` (R1–R5 flows, SoD, RLS, relay, consumer, RAG, webhooks) |
| 4 | Traceability | PASS | 23 rows, 22 implemented, CI gate green |
| 5 | RLS everywhere tenant-scoped | PASS | policies on all tenant tables; OutboxEvents/Tenants/AiInteractions? — AiInteractions HAS policy (tenant-scoped) ✓ |
| 6 | Migrations apply clean (dev+test) | PASS | 6 migrations, both DBs |
| 7 | Outbox→broker→consumer loop | PASS | relay lag 0 after k6; notification e2e in tests |
| 8 | PITR drill | PASS | 2026-09-11, marker recovered; RUNBOOK.md |
| 9 | Load test | PASS | k6 4006/4006, p95 ~15ms @10VUs (R1 paths; re-run for R2–R5 write mix before scale claims) |
| 10 | Observability | PASS (v1.1.0) | Grafana datasource+dashboard provisioned, 3 Prometheus alerts evaluating; OTLP traces need collector |
| 11 | Auth | PASS-local (v1.1.0) | Keycloak OIDC proven end-to-end (role→permission mapping, tenant claim, `RequireHttpsMetadata` non-prod exception); customer IdP = config task per `infra/keycloak/README.md` |
| 12 | Frontend | PASS-slice (v1.1.0) | 9 routes (R1–R5) build on Next 15.5.25; polish/i18n pending |
| 13 | K8s/air-gap prod install | PARTIAL (v1.1.0) | Helm chart lint+render verified, AIRGAP.md runbook; app Dockerfiles + migration Job pending |
| 14 | Pen-test | OPEN | automated baseline done (headers, vuln audit in `security-baseline.md`); human pen-test pending |
| 15 | Data migration tooling | PASS-slice (v1.1.0) | CSV directory import (dry-run + import) tested; SIS/ERP sync pending |
| 16 | Docs at target depth | OPEN | skeletons + implemented FRs only (01 target 250–350pp etc.) |

## Ship decision
Cleared for: demo, pilot onboarding design, IdP integration, R2–R5 frontend build.
NOT cleared for: production student data, public exposure, compliance sign-off.

Next (post-GA): 10–16 in roadmap order; k6 write-mix re-run; customer IdP (closes 11);
Helm + air-gap bundle (closes 13).
