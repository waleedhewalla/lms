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

## Shared rate limits (G5)
- With several API replicas the limit used to multiply (two replicas allowed twice the budget). The API now counts requests in Redis when `RateLimiting__Redis__ConnectionString` is set (the Helm chart sets it to its own Redis by default), so all replicas share one budget per signed-in user, per minute. Windows are aligned to the clock, so every replica agrees where a minute starts.
- Redis is optional and never in the way: unset, each replica counts for itself, as before; set but unreachable, the API logs a warning, counts per replica for ten seconds at a time, and keeps serving.
- The response to an over-limit caller is unchanged: 429 with `Retry-After`.
- The Helm default for trusted proxy networks now includes the IPv6 private range (`fc00::/7`), matching the code default.
- New dependency: `StackExchange.Redis` (maintained, MIT; passes the vulnerable-package gate).

## Encryption inside the cluster (G4)
- New Helm option `tls.enabled` (off by default): Postgres, Redis, RabbitMQ, MinIO and OpenSearch accept TLS only, and the API checks each one's certificate against the site CA and the service name. See `DEPLOYMENT.md` section 3c.
- Certificates from cert-manager (`tls.mode=certManager`, optionally your own `tls.issuerRef`) or from Secrets you create (`tls.mode=existingSecret`, with `infra/tls/gen-certs.sh`) for air-gapped sites.
- API: `Tls__CaFile` makes Postgres (`VerifyFull`), RabbitMQ (`RabbitMQ__Tls`), MinIO (`Storage__Secure`), OpenSearch and Redis (`ssl=true`) verified against that CA; the system roots are not used for them and checking is never switched off. The three RabbitMQ connection blocks now share one builder.
- Optional `networkPolicy.enabled`: only the API and the migration job may reach the data services.
- CI: the chart is rendered in both modes and checked for any plaintext path; a new `tls-integration` job tests Postgres and Redis with a private CA (right CA, wrong CA, plaintext refused).
- Known limits: server authentication only; OpenSearch is behind a TLS proxy rather than its security plugin; the ingress to web/API hop and the identity provider are the operator's. Not exercised end to end in CI: RabbitMQ, MinIO and OpenSearch run the same trust code (tested with a real TLS listener) but are not started with TLS in CI, and the chart has not been installed on a live cluster.

## Installable app (PWA), part 1: app shell
- The web app can now be installed to a phone's home screen or a desktop (manifest, icons, `start_url` = the module launcher).
- A small service worker shows a bilingual "You are offline" page instead of the browser's error when the network is gone. It stores only the offline page, the icons and the build's hashed static files. It never stores pages, API responses or tokens, so nothing personal is left on a shared device.
- The worker script and the offline page are always revalidated, so a fix reaches installed apps on the next visit. Not registered in development.
- Not yet: reading the inbox or approving while offline. That needs a decision on keeping personal data on the device (part 2, behind a switch, off by default).
- Fixed an end-to-end test from the module launcher work that matched Next's route announcer as well as the no-access panel.

## Correspondence registers
- Numbers now run per direction and year, as official registers do: `IN-2026-000001` (incoming), `OUT-...` (outgoing), `INT-...` (internal), restarting each year. The year is the institution's local year (`Calendar:UtcOffsetHours`, default +3), so a letter written just after midnight on 1 January joins the new year. Existing letters keep their `CORR-` numbers. This changes the format of numbers on new letters; the field name and type are unchanged.
- New register: `GET /api/correspondence/register` (filter by year, direction, status, search; paged) and `.../register/export` as a spreadsheet with Gregorian, Hijri or both dates. The register shows who received each letter and who got a copy, and never the letter body.
- Recipients can be To or Cc (copy). Existing recipients are To. One new column, no data change.
- **Security fix:** the thread and routing-slip endpoints did not apply the confidential check that the list and detail endpoints do, so a user without `correspondence:confidential` could read a confidential letter's content and directives through them, or attach a reply to it. All correspondence routes now treat a confidential letter as not found unless the caller is cleared, and a thread stops where the caller's clearance does.

## Correspondence screen on the real API
- The Correspondence page used to run on demo data and placeholder person IDs. It now reads and writes the real API: the register (year, direction, status, search, paging) with who the letter went to and who got a copy, new letters with external or directory recipients (To or Cc), replies that keep the thread, sending a draft for review (the reviewer decides in Approvals), directives (routing slips) with a forward-to person, and a CSV export with Gregorian and Hijri dates.
- Letters are written as the person the signed-in account is linked to (the token's `person_id`); an unlinked account can read but not write, and the page says so.
- The shared comments and activities panel (`Chatter`, also on Approvals and Meetings) defaulted to `http://localhost:5000` with no token, so it only worked on a developer machine. It now uses the app's API address and the signed-in token.
- Removed the Correspondence translation strings the old screen used.

## Personal calendar link
- On the Calendar page a signed-in person can create a private link and subscribe to it from a phone or Outlook (it refreshes about hourly). The feed holds the institution's dates and policy reviews, plus what concerns that person: meetings of their committees, their open tasks and the actions assigned to them. Other people's tasks and other committees' meetings are not in it.
- The link is a secret: it is shown once, stored only as a hash, expires after 90 days (`CalendarFeed__TokenDays`), and a new link replaces the old one. A person can turn it off at any time. A link stops working when the person is made inactive or erased. Creating and revoking are audited, never the link itself. One new table (`CalendarFeedTokens`, tenant-isolated).
- Anyone holding the link can read that person's calendar without signing in, so it should be treated like a password and used over https. All failures answer the same 404.
- Personal-data erasure now also deletes the person's calendar links.

## Offline My Work (installable app, part 2)
- Off by default. A deployment turns it on with `EDUNEXUS_OFFLINE_DATA=true` on the web container. With it off, nothing is stored on devices and My Work only reports that you are offline.
- With it on, My Work keeps a small copy on the device: counts, what is waiting for you to approve, and your open tasks (no letter text, notification bodies or tokens). Offline, the page opens from that copy with a banner. Access tokens last about 15 minutes and cannot be renewed offline, so My Work (and only My Work) opens offline even after the token has expired.
- Approving, rejecting or completing a task offline puts the action in a "Waiting to be sent" list. Nothing is sent automatically: after coming back online and signing in, the person reviews the list and presses "Send now" (this stops anyone else on a shared device from quietly acting in your name). The server checks each action again exactly as for an action taken online: one already decided by someone else, or no longer allowed, is dropped and reported, not retried.
- The copy and the list are removed on sign-out, when a different person signs in, after 7 days, and by "Clear offline copy".
- The service worker now also keeps the empty page shells of My Work and the module launcher and the runtime settings script (a test checks the built pages hold no personal data). The API and the identity provider are still never cached.

## Learning and student-information system connectors (backend)
- New, off by default (`Connectors__Enabled`): read-only links that bring the **course catalogue** of Moodle, Canvas, Sakai or Banner (Ethos) into EduNexus, plus a CSV import for any other system or for sites that prefer not to give a credential. EduNexus stays the governance layer beside those systems.
- Only courses are read: code, title, term, dates, a head count where the system reports one, and instructor names. No students, grades or submissions. A course the system stops listing is marked inactive, not deleted; a failed read keeps the last good catalogue and reports why (502 with a message, last result on the connector).
- Credentials are sealed at rest (AES-256-GCM, key `Connectors__SecretKey`, bound to the tenant and connector), never returned, and the other system is called without holding a database transaction. Addresses pass the webhook guard (no internal addresses unless `Integrations__AllowPrivateTargets`), redirects and cross-server next-page links are not followed, answers are size- and count-capped. Moodle's token goes in the request body, not the address.
- Two new tenant-isolated tables (`ConnectorConfigs`, `ExternalCourses`); additive migration. The Helm chart gains `api.connectors.enabled` (reads the key from the api Secret).
- **Not yet validated against a live system.** The four clients are tested against sample answers shaped like each system's documented API. Run each once against the pilot's own system; Sakai and Banner differ most between versions and give no counts or instructors. The screen for this is described in the next section.

## Connected systems screen
- New module **Connected systems** (Administration; needs `integration:read` to look and `integration:manage` to change): add a connection (system, address, credential and the few options each system takes), read it now, upload a CSV for a file connection, delete it, and browse the course catalogue with search, a connection filter, "only courses still listed" and paging.
- The credential field is a password box; after saving it is never shown again. A failed read shows the server's own explanation (for example "the server refused the credential (401)" or "could not be reached") and the last good catalogue stays. A rejected CSV names the rows at fault.
- When connectors are off on the server, the page says so and shows nothing else.

## Message-broker login (Helm)
- The chart never set a login for RabbitMQ, so the broker and the API both used the stock `guest/guest` account, which anyone who could reach the broker inside the cluster could use, even though the production checklist said to replace the RabbitMQ secret.
- New value `rabbitmq.auth.secretName`: name a Secret with `username` and `password` (`AIRGAP.md` has the command) and the broker creates that login instead of `guest`, and the API uses it. Left empty (the default), nothing changes, so existing installs keep working; set it before real data goes in, and combine it with `networkPolicy.enabled`.
- CI renders the chart both ways: nothing about it by default, and with the Secret named both the broker and the API reference it.

## Correspondence types (step 1 of 3: the catalogue)
- A university can now define the kinds of internal correspondence it runs. Nine standard types are created for a tenant by `POST /api/correspondence/types/seed-defaults` (safe to repeat; edits are kept): administrative memo, academic, financial, between departments, employee request, student request, decision, circular, and information request or complaint. Each has English and Arabic names, its purpose, who sends and receives, the permission needed to start one, and the workflow it will follow. The university can add types, rename them, switch them off and re-route them.
- A letter can carry a `typeCode`, and where a type needs them, a receiving unit (`targetUnitId`) and an `amount` (financial). A letter without a type works exactly as before; the three directions (incoming, outgoing, internal) and the numbering are unchanged. The register filters and exports by type.
- New permission `correspondence:student-request`: a student can start only a "student request", only as themselves, and no other kind of letter. The student request type is seeded switched off: students must not see anything but their own letters first, which is part of step 2.
- **Not yet in this step:** the workflows themselves (steps and approvers per type, returning a letter for changes, closing it, deadlines in working days and the notifications). Sending a letter still creates the single review it did before. The type's `workflowCode` names the workflow that step 2 defines.
- Data changes (additive only): new table `CorrespondenceTypes` (tenant-isolated); new nullable columns `TypeCode`, `TargetUnitId`, `Amount` on `Correspondences`; two new status values at the end of the list (`Returned`, `Closed`) that nothing uses yet.
- **For the university to confirm before step 2:** the amount limits for financial correspondence at each level (provisional values will be set and are editable), who closes each type (the recipient, the author or records), and whether a letter returned for changes restarts the whole workflow (assumed) or resumes at the step that returned it.

## Internal correspondence: workflows for every type (step 2 of 3)
- Each of the nine types now follows its own workflow from creation to closure (`docs/05-processes/correspondence-workflows.md` lists sender, receiver, purpose, steps, deadlines and conditions). The standard workflows are created by `seed-defaults` and can be edited by the university (`new-version`).
- New: return for changes (letter goes back to its author, who edits and resubmits), final rejection with a reason, withdrawal and closure with a reason, a full history of every letter, `mine` for each person's own letters, and in-app notifications to the author.
- The workflow engine gained conditions (financial limits by amount), skipping a step whose approver is the sender or already approved, deadlines in **working days** with the holiday calendar (weekend `Workweek__WeekendDays`, default Friday,Saturday), a closing step, and addressing a step to the receiving unit's head or the letter's recipient. Existing workflows (requests and others) behave exactly as before; every existing test passes unchanged.
- **Compatibility:** a letter without a type keeps the original path (one named reviewer, ends Approved). `reviewerId` on `submit` is now optional in the schema but still required for untyped letters. No migration in this step (new values are stored as text).
- **For the university to confirm:** the financial limits (50,000 and 250,000 are placeholders), who closes each type, whether a returned letter restarts from the first step, and whether the student request type is switched on. The screens follow in step 3.
- Not yet: the screens for all of this (step 3).

## Correspondence inside a college
- Three more standard types: between college administrations, between departments of a college, and to the dean or vice dean (line manager, then vice dean, then dean). `seed-defaults` now creates twelve types; tenants already seeded get the new ones by running it again. Workflow steps can now be addressed to the receiving unit's deputy (`target_deputy`).
- To confirm: whether the sender and receiver must be in the same college (not enforced yet), and whether the vice dean step is always required.

## Correspondence screens for types and workflows (step 3 of 3)
- The Correspondence page now offers a **letter type** when writing (it shows the type's purpose, sender and receiver, asks for the receiving unit or amount where the type needs them). Sending a typed letter starts its workflow; a letter with no type keeps the old reviewer picker.
- A letter shows its **path**: each step, who it is with, whether it is waiting, overdue, approved, returned or rejected, the deadline, who decided and their note, plus the full history. The author gets **Edit**, **Send**, **Send again** (after a return) and **Withdraw**; records staff get **Close**. A reason is required to withdraw or close.
- Students (who hold only `correspondence:student-request`) now see the Correspondence module and only **My letters**; they can write and send their own request and follow its path, but never see the register.
- When the server refuses something it chose to refuse (for example "no assignee found for role STUDENT_AFFAIRS"), the message is now shown instead of just the status number.
- API: an author may now open their own letter without the general read permission (`GET /api/correspondence/{id}`); new `GET /api/correspondence/units` lists the units a letter can be addressed to, for anyone who may write a letter (no directory permission needed).
- Tests: unit tests for the action rules, step states and history labels; three end-to-end specs on the real API (typed letter returned, edited, resent and withdrawn; student view; the earlier register flow). The end-to-end specs run locally and are not part of CI.

## Known limits
- A human pen-test (GA gate 14) must be run before production student data.
- Access tokens are kept in browser storage. The CSP mitigates this.
- The rate limit is protection against runaway clients, not a security boundary: put a limit on the ingress for anonymous traffic as well.
