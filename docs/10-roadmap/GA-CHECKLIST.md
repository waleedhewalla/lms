# GA release gates — v1.3.0 (delivery plan Waves A–G complete)

Scope honesty: this tag certifies a **working vertical slice of every release**,
not the full V2 document page-counts nor customer-specific production hardening.
Each gate below is PASS, PARTIAL, or OPEN (v1.0.0 → v1.1.0 deltas noted).

## Gates

| # | Gate | Status | Evidence |
|---|---|---|---|
| 1 | Solution builds 0 errors | PASS | `dotnet build EduNexus.sln` (warnings: NU1902 OTLP, NU1603 Prometheus float) |
| 2 | Unit tests | PASS | 6/6 `Foundation.Tests` |
| 3 | Integration tests (real PG+RMQ+OS+MinIO) | PASS | 30/30 `Api.Tests` (R1–R5 flows, SoD, RLS, relay, consumer, RAG, webhooks) |
| 4 | Traceability | PASS | 47 rows, 47 implemented (100% coverage), CI gate green |
| 5 | RLS everywhere tenant-scoped | PASS | policies on all tenant tables; OutboxEvents/Tenants/AiInteractions? — AiInteractions HAS policy (tenant-scoped) ✓ |
| 6 | Migrations apply clean (dev+test) | PASS | 6 migrations, both DBs |
| 7 | Outbox→broker→consumer loop | PASS | relay lag 0 after k6; notification e2e in tests |
| 8 | PITR drill | PASS | 2026-09-11, marker recovered; RUNBOOK.md |
| 9 | Load test | PASS | k6 4006/4006, p95 ~15ms @10VUs (R1 paths; re-run for R2–R5 write mix before scale claims) |
| 10 | Observability | PASS (v1.1.0) | Grafana datasource+dashboard provisioned, 3 Prometheus alerts evaluating; OTLP traces need collector |
| 11 | Auth | PASS-local (v1.1.0) | Keycloak OIDC proven end-to-end (role→permission mapping, tenant claim, `RequireHttpsMetadata` non-prod exception); customer IdP = config task per `infra/keycloak/README.md` |
| 12 | Frontend | PASS (v1.3.0) | 18 routes build on Next 15.5; every module has a bilingual (EN/AR, RTL) screen: My Work, Requests, Tasks, Approvals, Correspondence, Governance, Decisions, Policies, Calendar, Reports, Intelligence, Settings; Playwright smoke screenshots against seeded API |
| 13 | K8s/air-gap prod install | PASS (v1.3.0) | Helm chart has API, web, optional ingress, secrets-only credentials and non-root pods. CI lints and renders it on every PR. Both images build and are smoke-tested in CI. Covered by AIRGAP.md and DEPLOYMENT.md; the EF migration hook runs before API pods start. |
| 17 | Production auth in the web app | PASS (v1.3.0) | OIDC Authorization Code + PKCE sign-in and sign-out, tenant taken from the token, browser-verified against a mock IdP. The API refuses Production on dev auth (smoke-tested in CI). |
| 18 | Hardening | PASS (v1.3.0) | CORS restricted to configured origins (integration-tested); forwarded headers; security headers; non-root containers; runtime config (no secrets in images); `.env` ignored by git. |
| 19 | Release pipeline | PASS (v1.3.0) | Tagging `vX.Y.Z` publishes both images to GHCR and creates the GitHub release with RELEASE-NOTES.md. |
| 14 | Pen-test | IN-PROGRESS (Sprint 1) | Scope doc prepared; human pen-test scheduled for Sprint 1 window (SLA: Crit 24h, High 1w) |
| 15 | Data migration tooling | PASS-slice (v1.1.0) | CSV directory import (dry-run + import) tested; SIS/ERP sync pending |
| 16 | Docs at target depth | PASS-slice (v1.2.0) | Full architectural ADRs, runbooks (AIRGAP, IdP, SIS/ERP sync), traceability matrix, and API DTOs documented |

## Ship decision
Cleared for: demo, pilot deployment (Compose or Helm) with the institution's IdP, staff onboarding.
NOT cleared for: production student data, public exposure, compliance sign-off.

Open before production student data: gate 14 (pen-test), k6 write-mix re-run at the target
scale, and customer IdP configuration on site (gate 11). Deployment steps: `DEPLOYMENT.md`.
