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

## Accreditation and strategy (Wave I)
- **Standards library:** load NCAAA Institutional (8 standards), NCAAA Programmatic (6) or ABET engineering general criteria (8, with student outcomes 3.1–3.7) in one click. Built-in criteria are short summaries; paste the official criteria from your handbook as CSV (`standardCode,standardTitle,criterionCode,criterionText`) to replace them by code. A malformed file imports nothing.
- **Evidence crosswalk** (`/accreditation`): every criterion shows its evidence count, open findings and a status: Met, Partial (an open major or minor finding), or Not met (no evidence). Tick several criteria, even from different frameworks, and cite one record (document, policy, procedure, decision, meeting, committee, task, KPI or letter) against all of them at once. "Evidence used across criteria" lists the records that serve more than one criterion.
- **Findings and corrective actions:** actions move Open → In progress → Done → Verified. The assignee cannot verify their own action, and a finding closes only when all its actions are verified.
- **Self-study report:** a printable HTML report per framework, in English or Arabic (RTL). It has a readiness summary, then each standard and criterion with its status, cited evidence (with live KPI values), findings and actions, dated in Gregorian and Hijri.
- **Strategy scorecard** (`/strategy`): KPIs have a baseline and a direction ("lower is better" for rates such as dropout). Status is Achieved, On track (≥75% of the way from baseline to target), At risk (≥50%) or Off track. Each reading is kept with its period, so the scorecard shows a trend; KPIs cited as accreditation evidence are flagged.
- New Keycloak group `edunexus-quality-officers`. Migration R18 adds `KpiReadings` (row-level security), `Standards.Framework`, and `Kpis.Baseline` / `Kpis.LowerIsBetter`.

## Board (Wave J)
- **Votes belong to the voter.** A member votes for themselves; the secretary (`meeting:create`) may record a vote cast in the room, and the vote keeps who recorded it. Only current members who are marked present and have not declared a conflict can vote. Previously any caller could submit a vote for any person ID.
- **Quorum per committee:** a simple majority of current members by default (previously half, rounded up), or at least a set percentage (e.g. 66.67 for two-thirds). Members whose term has ended no longer count.
- **Item results** (`/api/meetings/{id}/results`): tally, item quorum and outcome (Carried, Failed, Tied, No quorum). Abstentions do not count against.
- **Conflicts of interest:** a member declares an interest on an agenda item before voting and is excluded from that vote and that item's quorum. An annual **interest register** per committee shows who has declared and who is missing.
- **Written resolutions** (`/board`): the secretary circulates a resolution with a closing time; members are notified and respond In favour, Against, Abstain or Recuse. It passes with a majority of all eligible members (or all of them when unanimous is required) as soon as the result is certain, fails when it no longer can pass, and expires at the deadline. Closed resolutions produce a printable certificate listing every member's response.
- Migration R19 adds `AgendaRecusals`, `InterestDeclarations`, `CircularResolutions` and `ResolutionVotes` (all under row-level security), `Committees.QuorumPercent` and `MeetingVotes.RecordedBy`.

## Policy attestation (Wave J)
- **Version-bound acknowledgements:** an acknowledgement records the policy version. Publishing a new version asks everyone to acknowledge again. Existing acknowledgements are carried over to each policy's current version on upgrade (migration R20), so nobody is asked twice by the upgrade itself.
- **Re-attestation cycle:** a policy can require re-acknowledgement every N months (e.g. 12 for an annual code-of-conduct sign-off).
- **Comprehension quiz:** policy owners add up to 20 multiple-choice questions and a pass mark. People read the policy and answer before acknowledging. Failed attempts are audited and store nothing; answers are never shown to takers.
- **My attestations** on the Policies page lists what each person still has to do and why (never, new version, cycle due). Owners see **attestation compliance** per policy and can send in-app reminders to everyone outstanding.
- The governance dashboard and policy report count only current attestations.

## Storage safety (G6)
- **File storage can no longer fail silently.** The MinIO SDK was seen to report success against an unreachable server, so the API now proves each claim itself with a signed request:
  - the bucket is checked before an upload URL is issued (otherwise 503);
  - an uploaded file must exist, with the declared size, before a document version, task evidence or decision-action evidence refers to it (otherwise 422, or 503 if storage cannot answer);
  - a deletion is confirmed only when storage says the object does not exist: a missing bucket no longer counts as "deleted".
- Uploaded file names are reduced to a safe leaf (no paths, `..` or control characters).
- Records disposal logs storage failures as errors and leaves the item pending, as before.
- Tests no longer depend on a MinIO server (an in-memory fake with scripted failures), and a test fails the build if an empty `catch { }` is added to the backend.
- Known limits: the SHA-256 a client declares is recorded, not recomputed; files attached as evidence are not removed by records disposal.

## Known limits
- A human pen-test (GA gate 14) must be run before production student data.
- Access tokens are kept in browser storage. The CSP mitigates this.
- Rate limits are kept in memory per API instance. With several replicas, the effective limit is the per-instance limit times the replica count.
