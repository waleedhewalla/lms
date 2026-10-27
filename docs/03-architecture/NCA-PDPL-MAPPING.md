# NCA ECC-2:2024 and PDPL compliance mapping

This maps EduNexus (v1.3.x and later) to:
- the National Cybersecurity Authority's **Essential Cybersecurity Controls (ECC-2:2024)**;
- the **Cloud Cybersecurity Controls (CCC-2:2024)**;
- the **Personal Data Protection Law (PDPL)** and its Implementing Regulations.

It is written for an institution's cybersecurity and data-protection teams, and for auditors.

> **Read this first.**
> - This is an engineering mapping, not legal advice or a certification.
> - It was prepared from secondary sources because the official NCA and SDAIA texts couldn't be opened from the build environment. ECC subdomains are therefore referenced **by name**, not by number.
> - Before relying on any line for compliance work, check the control wording and numbering against the official documents on nca.gov.sa and sdaia.gov.sa.
> - "Evidence" points to code, configuration, tests or documents in this repository.

## 1. Who is responsible for what

EduNexus is software that the institution deploys on its own infrastructure: on-premises, air-gapped, or an approved cloud. Responsibilities divide as follows:

| Party | Responsible for |
|---|---|
| **EduNexus (the product)** | Security features built into the software: authentication integration, authorization, tenant isolation, audit, retention/disposal, secure defaults, a secure development pipeline |
| **The institution (operator and data controller)** | Governance, policies, people, risk management, its identity provider and users, hosting, network, backups in practice, monitoring operations, incident response, PDPL controller duties |
| **Hosting / cloud provider** (when used) | Physical and infrastructure controls; must meet CCC-2:2024 and the data-hosting rules for the data's classification |

Status legend:
- ✅ **Built in:** implemented and tested in the product.
- 🟡 **Configure:** the product supports it; the operator must configure or operate it.
- 🔧 **Operator:** outside the software; the institution's process or infrastructure.
- ❌ **Gap:** not yet supported; see §6.

## 2. ECC-2:2024 by domain

ECC-2:2024 has 4 main domains: Governance, Defense, Resilience, and Third-Party & Cloud Computing. It has 28 subdomains and 108 main controls, down from 5 / 29 / 114 in ECC-1:2018.

### 2.1 Cybersecurity Governance

| Subdomain | Status | How EduNexus supports it / evidence |
|---|---|---|
| Strategy, management, policies & procedures, roles & responsibilities | 🔧 | Institution's governance. EduNexus can host the institution's own cybersecurity policies with versioning, review/approval workflow and staff acknowledgements (`/policies`, `GovernanceDepthEndpoints.cs`). |
| Risk management | 🔧 | Institution's process. Product-level threat model inputs: `docs/03-architecture/PENTEST-SCOPE.md`. |
| Cybersecurity in IT projects (secure development) | ✅ | Every change runs CI: unit and integration tests, row-level-security and cross-tenant tests, `dotnet list package --vulnerable` and `npm audit --audit-level=high` (both block merges), container image build and smoke tests, Helm lint. See `.github/workflows/ci.yml` and `docs/03-architecture/security-baseline.md`. |
| Compliance with laws and regulations | 🟡 | This document; records retention per the Law of Documents and Archives (§2.2 "Data and information protection"); Hijri dating of official records. |
| Periodic review and audit | 🟡 | Immutable audit trail of business actions (`AuditEvent`, `/api/audit`, the `/audit` page). Access reviews: role assignments are queryable via `/api/roles`. Running the reviews is the operator's job. |
| Cybersecurity in human resources | 🔧 | Institution. Leavers lose access when disabled in the identity provider (tokens expire within 15 minutes; see the Keycloak realm). |
| Awareness and training | 🔧 / 🟡 | Institution. Policy acknowledgements record that staff have read the security policies. |

### 2.2 Cybersecurity Defense

| Subdomain | Status | How EduNexus supports it / evidence |
|---|---|---|
| Asset management | 🔧 / 🟡 | Software bill of materials: the NuGet/npm lockfiles; container images built per release tag (`.github/workflows/release.yml`). Infrastructure inventory is the operator's. |
| Identity and access management | ✅ / 🟡 | See the IAM details below this table. |
| Information system and processing facilities protection | ✅ / 🔧 | Non-root containers; all Linux capabilities dropped in Helm; no secrets in images, all supplied through runtime environment/Secrets (`infra/helm/edunexus/templates`, `docs/03-architecture/secrets-management.md`). Host hardening, anti-malware and patching are the operator's. |
| Email protection | 🔧 | EduNexus does not run mail infrastructure. Notification email goes through the institution's relay. |
| Network security management | 🟡 | See the network details below this table. |
| Mobile devices security | 🔧 | No native mobile app yet; the web app is responsive. Device management is the institution's. |
| Data and information protection | ✅ / 🟡 | See the data-protection details below this table. |
| Cryptography | 🟡 / ❌ | TLS terminates at the institution's ingress or reverse proxy (`DEPLOYMENT.md`). The identity provider requires HTTPS in Production (`RequireHttpsMetadata`). Webhooks are signed with HMAC (`WebhookSigner.cs`). Encryption at rest: database and object-storage volumes must be encrypted by the operator. Service-to-service TLS inside the cluster is not enforced by the chart (❌ G4). |
| Backup and recovery management | 🟡 | PostgreSQL base backups plus WAL archiving for point-in-time recovery, with a recovery drill recorded in `infra/postgres/RUNBOOK.md`. Scheduling, off-site copies and restore testing are the operator's. |
| Vulnerability management | ✅ / 🔧 | CI blocks merges on vulnerable dependencies. Example: Next.js was upgraded within the same day a critical RCE advisory (GHSA-2464-fhr4-w9xw) was caught. Patching running sites is the operator's job: redeploy release images. |
| Penetration testing | 🟡 | Scope and rules of engagement in `PENTEST-SCOPE.md`. A human pen-test is required before production student data (GA gate 14). |
| Cybersecurity event logs and monitoring | ✅ / 🟡 | Business audit log (who did what, when) on every state change, written in the same transaction as the change through `DomainEvents.Record`. Prometheus metrics with alerts for API down, error rate and slow p95 (`infra/prometheus/alerts.yml`); OpenTelemetry traces. `/metrics` answers only internal callers. Collecting logs into the institution's SIEM is the operator's job. |
| Cybersecurity incident and threat management | 🔧 / 🟡 | Breach-response runbook in §5. Audit and log data support investigations. |
| Physical security | 🔧 | Operator or hosting provider. |
| Web application security | ✅ | See the web-application details below this table. |

**Identity and access management in detail:**
- **Single sign-on:** OIDC through the institution's identity provider. A ready Keycloak realm is provided (`infra/keycloak/realm-edunexus.json`).
- **Multi-factor authentication:** **on by default in that realm.** Every new user must enrol an authenticator app, and every sign-in asks for the code. This was verified in a browser against Keycloak 26.
- **Least privilege:** 75 fine-grained permissions grouped into role groups, checked on every endpoint.
- **Segregation of duties:** applies to approvals, task verification and records disposal.
- **Brute-force lockout** and a password policy: at least 12 characters, not the username or email, last 5 passwords cannot be reused.
- **Short sessions:** 15-minute access tokens with refresh-token rotation.
- **Lifecycle:** joiner, mover and leaver changes are made in the identity provider.

**Network security management in detail:**
- Ingress and TLS are configured by the operator.
- The API adds:
  - CORS limited to the configured web origin;
  - `X-Forwarded-*` trusted only from private-network proxies;
  - rate limiting: 600 requests a minute per user, plus 60 a minute on expensive endpoints;
  - an SSRF guard on outbound webhooks (internal addresses blocked at registration and at connect time).
- Network segmentation and the firewall are the operator's.

**Data and information protection in detail:**
- **Tenant isolation:** PostgreSQL row-level security, `FORCE`d on every tenant table, plus a cross-tenant token check on every request. Both are covered by tests.
- **Classification:** document classification (Public, Internal, Confidential, Restricted) and confidential correspondence, which is visible only with `correspondence:confidential`.
- **Retention and disposal:** schedules, legal holds, and two-person disposal with **confirmed** deletion, recorded in a permanent disposal register (`RecordsEndpoints.cs`).
- **File access:** stored files are reachable only through short-lived signed URLs, and storage keys are scoped to the tenant.
- **Gap:** data-loss-prevention tooling is the operator's.

**Web application security in detail:**
- **Browser protections:** per-request Content-Security-Policy, nosniff, frame denial, no-referrer, permissions policy.
- **Input handling:** EF Core parameterised queries; formula-safe CSV exports.
- **Sign-in:** OIDC Authorization Code + PKCE; the post-sign-in redirect is restricted to the same origin.
- **Production safety:** the API refuses to start in Production with developer authentication.
- **Testing:** OWASP areas are covered in `PENTEST-SCOPE.md`.

### 2.3 Cybersecurity Resilience

| Subdomain | Status | Evidence |
|---|---|---|
| Cybersecurity in business continuity management | 🟡 | Stateless API and web tiers that scale horizontally (Helm `replicas: 2`). Database migrations run as a pre-upgrade job. Point-in-time recovery runbook. Disaster-recovery site, RTO/RPO targets and drills are the operator's. |

### 2.4 Third-Party and Cloud Computing Cybersecurity

| Subdomain | Status | Evidence |
|---|---|---|
| Third-party cybersecurity | 🟡 | Third-party code is limited to open-source libraries, scanned on every change. External services are **off by default**: the AI provider defaults to `Echo` (no external calls). If an OpenAI-compatible endpoint is configured, prefer an on-premises or in-Kingdom endpoint (see PDPL cross-border, §4). Webhooks are created by tenant admins and protected against SSRF. |
| Cloud computing cybersecurity | 🟡 | See §3. |

## 3. Cloud hosting and data location (CCC-2:2024 / NDMO)

- **Data localisation has moved.** ECC-2:2024 no longer prescribes data localisation directly; authority moved to the National Data Management Office (NDMO) at SDAIA. Government agencies and critical-infrastructure operators hosting in the cloud fall under CCC-2:2024, and in-Kingdom hosting depends on the data's classification.
- **Deployment options:**
  - **On-premises or air-gapped** (`infra/helm/edunexus/AIRGAP.md`): all data, backups, keys and logs stay with the institution.
  - **Cloud:** deploy only on a provider and region registered for the required data class, such as a CST-registered provider with a Saudi region.
- **Everything stays in the institution's chosen location:**
  - database, object storage, search index and message broker;
  - backups and WAL archives;
  - encryption keys;
  - logs and metrics.
- **No outbound calls by default.** EduNexus phones home to nobody. The AI provider and webhooks are the only outbound integrations, and both are off unless configured.

## 4. PDPL (Personal Data Protection Law)

**Roles under PDPL:**
- The **institution is the controller.**
- **On-premises:** the product vendor does not process the data, because it has no access.
- **Managed hosting:** if the vendor or a partner operates the system for the institution, a processor agreement is required.

| Obligation | Status | How EduNexus supports it / evidence |
|---|---|---|
| Lawful basis, purpose limitation | 🔧 | Institution's determination. Typical bases: employment, public-interest functions and contract. EduNexus processes staff and governance data for institutional operations. |
| Data minimisation | 🟡 | People records hold only directory fields (name, type, email, organisation). Avoid putting special-category data in free-text fields; policy and training are the institution's job. |
| Accuracy, right to correction | ✅ | People records are editable (`PATCH /api/people/{id}`, `person:update`), and changes are audited. |
| Right of access / to obtain a copy | ✅ | Settings → **My data → Download my data** (self-service), or a privacy officer exports any person (`GET /api/people/{id}/personal-data`, `privacy:manage`). The JSON file covers the person and 30 related record types; correspondence is exported as metadata only because its content is institutional. Every export is audited (`PersonalDataExported`). |
| Right to destruction | ✅ | Records retention and disposal covers correspondence and documents. Person erasure (`/api/privacy/erasure-requests`) pseudonymises the directory record, deletes role assignments, delegations, notifications, follows and shares, renames correspondence participants, and redacts AI excerpts. A second privacy officer must approve, and legal holds block it. Votes, minutes and the audit trail are kept under the pseudonym. The identity-provider account is removed separately. |
| Respond to requests within 30 days (extendable by 30) | 🔧 | Institution's process. Use EduNexus requests and tasks with an SLA policy to track data-subject requests. |
| Retention limits | ✅ | Retention schedules with legal references; disposal register (`/records`). |
| Security of processing | ✅ / 🟡 | All of §2 above. |
| Breach notification: SDAIA within 72 hours of awareness; data subjects without undue delay | 🔧 / 🟡 | Runbook in §5. Audit trail and logs support scoping the breach. |
| Data protection officer | 🔧 | Required in specific cases, including public entities whose services involve large-scale processing. Institution's appointment. |
| Records of processing activities | 🔧 / 🟡 | Institution's register. §7 lists the personal data EduNexus stores, as input to that register. |
| Cross-border transfer | ✅ / 🟡 | Nothing leaves the deployment by default. Configuring an external AI endpoint or a webhook to an external system can be a cross-border transfer: assess it first, and prefer in-Kingdom or on-premises endpoints. |
| Privacy notice to data subjects | ✅ | The institution publishes its notice as a policy with code `PRIVACY`, so it gets versioning and approval like any other policy. It is shown without sign-in at `/privacy?tenant=<slug>`, served by `GET /api/public/privacy-notice`, and linked from every page. |

## 5. Breach response runbook (PDPL 72-hour clock)

1. **Detect and record.** Note the time the institution became aware: the 72-hour clock to notify SDAIA starts then. Open an incident in the institution's process.
2. **Contain.** Disable affected accounts in the identity provider; tokens expire within 15 minutes. Rotate the secrets in `edunexus-api` and `edunexus-minio` (`DEPLOYMENT.md`). Block the source at the ingress.
3. **Scope.** Use the EduNexus audit log (`/audit`, `GET /api/audit`) to see who accessed or changed what and when. Then use the API logs, ingress logs and Prometheus or traces. Row-level security limits blast radius to a tenant unless the database role itself was compromised.
4. **Assess harm.** Is the breach likely to harm data subjects or conflict with their rights? Which data categories are affected (see §7)?
5. **Notify.**
   - **SDAIA, within 72 hours:** describe what happened, the data and people affected, the likely consequences, the measures taken and planned, and the DPO's contact details.
   - **Affected people, without undue delay** where there is a risk of harm, with what to do to protect themselves.
6. **Recover and learn.** Restore from point-in-time recovery if data integrity was affected (`infra/postgres/RUNBOOK.md`), fix the root cause, re-test, and update this mapping and the risk register.

## 6. Gaps and backlog

| ID | Gap | Proposed fix | Priority |
|---|---|---|---|
| G1 | ~~No one-click personal-data export~~ | **Done:** self-service and privacy-officer export, audited | ✅ |
| G2 | ~~No person-level erasure or pseudonymisation~~ | **Done:** erasure requests with two-person approval, a legal-hold check and pseudonymisation; institutional records are kept | ✅ |
| G3 | ~~No privacy notice in the web app~~ | **Done:** published `PRIVACY` policy shown publicly at `/privacy` | ✅ |
| G4 | No enforced TLS inside the cluster | A Helm option for TLS or a service mesh (mTLS) between API, database, broker, storage and search; document Postgres `sslmode=require` | Medium |
| G5 | Rate limits are per instance | A shared rate-limit store (Redis is already in the stack) when running more than one API replica | Low |
| G6 | MinIO SDK may hide errors (found during records work) | Audit the remaining storage calls (e.g. `EnsureBucket`) and verify their results independently, as disposal now does | Medium |
| — | National e-signature (Nafath / UAE PASS) | Needs a licensed trust service provider (e.g. emdha); waiting on a vendor decision | Product roadmap |

## 7. Personal data held by EduNexus (input to the records of processing)

| Category | Where | Examples |
|---|---|---|
| Directory and identity | People, role assignments, delegations | Name, person type, work email, organisation unit, roles, delegation periods |
| Work activity | Tasks, approvals, requests, form submissions | Assignments, decisions, comments, request content entered by staff |
| Governance | Committees, meetings, votes, minutes, decisions | Membership, attendance, votes, action owners |
| Communications | Correspondence, announcements, notifications | Authors and recipients, letter content, notification preferences |
| Documents | Documents and versions (object storage) | Whatever staff upload: classify, and set retention schedules |
| Security and audit | Audit events, identity-provider records | Who did what and when; sign-in data stays in the identity provider |
| AI interactions | AI interaction log | Prompts and answers, when AI features are used |

## Sources

All are secondary sources, to be verified against official NCA and SDAIA texts.
- [NCA ECC-2:2024 overview](https://khanmarshal.duckdns.org/nca-ecc/overview)
- [ECC-2:2024 changes](https://www.secure.com/blog/compliance/ecc-2-2024-july-2025-changes-gcc-security-program)
- [ECC update for Saudi businesses](https://www.secure.com/blog/compliance/saudi-businesses-nca-ecc-update)
- [CyberArrow on NCA ECC-2](https://www.cyberarrow.io/?p=20803)
- [Bird & Bird on Saudi cybersecurity regulation](https://cm.twobirds.com/en/insights/2024/ksa-movement-in-saudi-arabias-cybersecurity-regulatory-regime)
- [Cybersecurity Regulations for Cloud Computing](https://qanoniah.com/en/File/daJBw8v14neleZZ7Agerk5m6D-Cybersecurity-Regulations-for-Cloud-Computing)
- [NCA cloud controls guide](https://cdn.nca.gov.sa/api/files/public/upload/a341fa97-dd72-476a-9ea0-2545edd1d90c_GCCC.pdf)
- [Clyde & Co on the PDPL Implementing Regulations](https://www.clydeco.com/en/insights/2023/09/saudi-arabia-issues-implementing-regulations)
- [Clyde & Co: countdown to PDPL compliance](https://www.clydeco.com/en/insights/2024/01/countdown-to-compliance-with-saudi-arabia-pdpl)
- [Latham & Watkins: the PDPL enters into force](https://www.lw.com/en/insights/2023/12/Saudi-Arabias-data-protection-law-enters-into-force)
- [US trade note on PDPL cross-border rules](https://www.trade.gov/market-intelligence/saudi-arabia-ict-cross-border-data-transfer-rules-now-under-enforcement)
