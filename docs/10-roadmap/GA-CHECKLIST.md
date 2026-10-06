# GA release gates — v1.3.0 (delivery plan Waves A–G complete)

Scope honesty: this tag certifies a **working vertical slice of every release**,
not the full V2 document page-counts nor customer-specific production hardening.
Each gate below is PASS, PARTIAL, or OPEN (v1.0.0 → v1.1.0 deltas noted).

## Gates

| # | Gate | Status | Evidence |
|---|---|---|---|
| 1 | Solution builds 0 errors | PASS | `dotnet build EduNexus.sln` (warnings: NU1902 OTLP, NU1603 Prometheus float) |
| 2 | Unit tests | PASS | 10/10 `Foundation.Tests` |
| 3 | Integration tests (real PG+RMQ+OS+MinIO) | PASS | 47 `Api.Tests` across R1–R5 and Waves A–G: SoD, RLS, relay, consumer, RAG, webhooks, CORS, SSRF guard, tenant-scoped storage keys. All green in CI. |
| 4 | Traceability | PASS | 72 rows, 72 implemented (100% coverage), CI gate green |
| 5 | RLS everywhere tenant-scoped | PASS | policies on all tenant tables; OutboxEvents/Tenants/AiInteractions? — AiInteractions HAS policy (tenant-scoped) ✓ |
| 6 | Migrations apply clean (dev+test) | PASS | 6 migrations, both DBs |
| 7 | Outbox→broker→consumer loop | PASS | relay lag 0 after k6; notification e2e in tests |
| 8 | PITR drill | PASS | 2026-09-11, marker recovered; RUNBOOK.md |
| 9 | Load test | PASS (v1.3.0) | Re-run on 2026-10-06 at 10 virtual users on a single 4-vCPU host. **R1 baseline:** 4,064 requests, 0% failed, p95 7.9 ms. **R2–R5 write mix** (40% reads: inbox counts and the executive dashboard; 60% writes: correspondence, document tags, votes, activities): 3,633 requests, 0% failed, 100% of checks passed, p95 16.9 ms, p99 24 ms. The run found and fixed a 500 on duplicate votes (now 409) and repaired the stale k6 script. Re-run at the target concurrency before making scale claims. |
| 10 | Observability | PASS (v1.1.0) | Grafana datasource+dashboard provisioned, 3 Prometheus alerts evaluating; OTLP traces need collector |
| 11 | Auth | PASS (v1.3.0) | Turnkey Keycloak realm (`infra/keycloak/realm-edunexus.json`): PKCE web client, claim mappers, permission groups, brute-force protection and a password policy. CI imports it and verifies the claims. Proven end to end: a browser signed in on Keycloak 26's login page with PKCE S256, and the API in OIDC mode accepted the token. A customer IdP other than Keycloak is configured per `infra/keycloak/README.md`. |
| 12 | Frontend | PASS (v1.3.0) | 18 routes build on Next 15.5; every module has a bilingual (EN/AR, RTL) screen: My Work, Requests, Tasks, Approvals, Correspondence, Governance, Decisions, Policies, Calendar, Reports, Intelligence, Settings; Playwright smoke screenshots against seeded API |
| 13 | K8s/air-gap prod install | PASS (v1.3.0) | Helm chart has API, web, optional ingress, secrets-only credentials and non-root pods. CI lints and renders it on every PR. Both images build and are smoke-tested in CI. Covered by AIRGAP.md and DEPLOYMENT.md; the EF migration hook runs before API pods start. |
| 17 | Production auth in the web app | PASS (v1.3.0) | OIDC Authorization Code + PKCE sign-in and sign-out, tenant taken from the token, browser-verified against a mock IdP. The API refuses Production on dev auth (smoke-tested in CI). |
| 18 | Hardening | PASS (v1.3.0) | CORS restricted to configured origins (integration-tested); forwarded headers; security headers; non-root containers; runtime config (no secrets in images); `.env` ignored by git. |
| 19 | Release pipeline | PASS (v1.3.0) | Tagging `vX.Y.Z` publishes both images to GHCR and creates the GitHub release with RELEASE-NOTES.md. |
| 14 | Pen-test | READY FOR TESTERS | Scope and rules of engagement are in `docs/03-architecture/PENTEST-SCOPE.md`. Automated pre-checks are done and enforced in CI: 0 vulnerable NuGet/npm packages, SSRF guard, CSP, tenant-scoped storage keys, CORS, non-root images. **Still needs human testers**; the SLA is Critical 24 h, High 1 week. |
| 15 | Data migration tooling | PASS-slice (v1.1.0) | CSV directory import (dry-run + import) tested; SIS/ERP sync pending |
| 16 | Docs at target depth | PASS-slice (v1.2.0) | Full architectural ADRs, runbooks (AIRGAP, IdP, SIS/ERP sync), traceability matrix, and API DTOs documented |

## Ship decision
Cleared for: demo, pilot deployment (Compose or Helm) with the institution's IdP, staff onboarding.
NOT cleared for: production student data, public exposure, compliance sign-off.

Open before production student data: gate 14 (human pen-test against `PENTEST-SCOPE.md`) and
the site's own IdP users and groups. Deployment steps: `DEPLOYMENT.md`.
