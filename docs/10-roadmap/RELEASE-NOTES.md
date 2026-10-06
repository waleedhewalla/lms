# EduNexus v1.3.0

Feature-complete release of the delivery plan (Waves A–G).

## Backend
- **Work management:** `auth/me` and permissions, people updates, direct reports, task creation and verification, submitted approvals, inbox counts, SLA compliance, communication archive.
- **Reports and dashboards:**
  - executive, department and governance dashboards;
  - my-performance and SLA reports, plus named reports;
  - CSV export that is formula-safe.
- **Governance depth:**
  - institutional calendar with `.ics` export;
  - committee terms, meeting agenda, check-in, minutes and minutes approval;
  - decision lifecycle with action evidence and verification;
  - policy versions and the review → legal → approval → publish workflow, review scheduling, acknowledgements, procedures;
  - notification preferences (channels, priority floor, quiet hours) and SLA policies.
  - Migration R15 adds 7 tenant tables, all under row-level security.
- **AI assistants:** meeting summaries, draft minutes, decision extraction from minutes, search suggestions, institutional memory. AI is pluggable: Echo (offline) or any OpenAI-compatible endpoint, with an extractive fallback.

## Web
- Bilingual (EN/AR, RTL) screens for every module: My Work, Requests, Tasks, Approvals, Correspondence, Governance, Decisions, Policies, Calendar, Reports, Intelligence, Directory, Roles, Audit, Settings.
- Production sign-in through the institution's IdP (OIDC Authorization Code + PKCE). The tenant comes from the token.
- Runtime configuration lets one image serve every environment.

## Deployment
- Compose app tier (`infra/docker-compose.app.yml`) with a migration step, plus `.env.example`.
- Helm chart:
  - web deployment and optional ingress;
  - credentials from Secrets (no passwords in values);
  - non-root pods with all capabilities dropped.
- Configurable CORS, which denies cross-origin requests by default outside Development; forwarded headers honoured behind proxies.
- CI builds both images, smoke-tests them, and lints/renders the chart. Tagging `vX.Y.Z` publishes images to GHCR and creates the GitHub release.
- Deployment guide: `docs/10-roadmap/DEPLOYMENT.md`.

## Security and readiness
- **Turnkey Keycloak realm:**
  - PKCE web client, claim mappers and permission groups;
  - brute-force lockout and a password policy;
  - verified in CI and proven end to end with a real browser sign-in.
- **Dependencies:** zero vulnerable dependencies, with OpenTelemetry 1.19 and Next 15.5.27. CI now blocks vulnerable packages.
- **Webhook SSRF guard:** checks at registration and again at connect time; no redirects; random signing secrets.
- **Storage keys:** keys sent by clients must sit under the tenant's own prefix.
- **Web hardening:** per-request Content-Security-Policy, and a stricter same-origin check on the post-sign-in redirect.
- **Duplicate votes:** a second vote on the same agenda item now returns 409 instead of a 500.
- **Load test:** 0% errors at 10 virtual users. R1 baseline p95 7.9 ms; R2–R5 write mix p95 16.9 ms.
- **Pen-test:** scope document `docs/03-architecture/PENTEST-SCOPE.md`.

## Known limits
- A human pen-test (GA gate 14) must be run before production student data.
- Access tokens are kept in browser storage. The CSP mitigates this.
- There is no API rate limiting; set it at the ingress or proxy.
