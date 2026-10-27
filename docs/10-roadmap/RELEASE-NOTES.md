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
- **Rate limiting in the API:** 600 requests a minute per user (per IP when anonymous), plus 60 a minute for AI, search, exports and token minting. Over-limit calls get 429 with `Retry-After`.
- **Trusted proxies:** `X-Forwarded-For` is honoured only from private-network proxies, so callers can't spoof their IP. `/metrics` answers internal callers only.
- **Duplicate votes:** a second vote on the same agenda item now returns 409 instead of a 500.
- **Load test:** 0% errors at 10 virtual users. R1 baseline p95 7.9 ms; R2–R5 write mix p95 16.9 ms.
- **Pen-test:** scope document `docs/03-architecture/PENTEST-SCOPE.md`.

## Wave H (Gulf readiness), part 1: Hijri dates
- Dates throughout the app can show Gregorian, Hijri (Umm al-Qura) or both. Arabic users default to both; Settings changes it.
- Date inputs show the Hijri equivalent as you pick.
- Report CSV exports accept `?calendar=hijri|both`, which substitutes or adds Umm al-Qura date columns computed in Saudi time.
- Fix: the interface language could revert to English after a quick reload. It is now saved only when the user changes it.

## Wave H, part 2: records retention and disposal
- Retention schedules per record type (correspondence, documents), with a record class, retention period, outcome (destroy, transfer to the National Center for Archives and Records, or keep permanently) and legal reference.
- Legal holds keep a record out of disposal, including holds placed after a batch was proposed.
- Disposal batches: proposed by a records officer and approved by a **different** person.
  - Destroying correspondence redacts its content and keeps the register entry.
  - Destroying a document deletes its stored files and **confirms** the deletion; anything unconfirmed stays "storage pending" and can be retried.
- A CSV manifest with Hijri dates serves as the disposal certificate or archive handover list.
- New **Records Retention** screen, a records-officers Keycloak group, and four new permissions (`records:read|manage|approve|hold`).
- Field labels are now linked to their inputs, for screen readers.

## Wave H, part 3: NCA / PDPL readiness
- `docs/03-architecture/NCA-PDPL-MAPPING.md` maps the product against NCA ECC-2:2024 (by domain), CCC-2:2024 and data location, and the PDPL. Each item is marked built in, configure, operator responsibility, or gap. It also includes a 72-hour breach runbook, a personal-data inventory, and a gap backlog (G1–G6).
- Multi-factor authentication is **on by default** in the Keycloak realm: users enrol an authenticator app at first sign-in and enter a code at every sign-in.

## Privacy (PDPL): access and transparency
- **Download my data** in Settings exports everything EduNexus holds about the signed-in person as JSON: the person record plus 30 related record types. Privacy officers (`privacy:manage`, a new Keycloak group) can export for any person. Every export is audited.
- **Privacy notice:** publish a policy with code `PRIVACY` and it appears without sign-in at `/privacy?tenant=<slug>`, linked from every page.
- **Erasure (right to destruction):** a privacy officer proposes erasing a person in Settings, and a second officer approves. Executing it replaces the name with a pseudonym ("Former member XXXXXXXX"), clears the email and rank, deactivates the person, and deletes their role assignments, delegations, notifications, preferences, follows and document shares. Votes, approvals, minutes and the audit trail are kept under the pseudonym. Erasure is refused while correspondence involving the person is under a legal hold. Migration R17 adds one tenant table under row-level security.

## Known limits
- A human pen-test (GA gate 14) must be run before production student data.
- Access tokens are kept in browser storage. The CSP mitigates this.
- Rate limits are kept in memory per API instance. With several replicas, the effective limit is the per-instance limit times the replica count.
