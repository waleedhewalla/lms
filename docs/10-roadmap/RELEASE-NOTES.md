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

## Known limits
- Pen-test (GA gate 14) is still to be run by humans before production student data.
- Access tokens are kept in browser storage; pair with a strict CSP at the proxy.
