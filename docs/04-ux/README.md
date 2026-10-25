# 04 — UX & Screen Specification

Target: 150–200 screens. Template per screen: ID, users, purpose, entry points, layout, components, filters/search, actions, permissions, states (empty/loading/error), validation, notifications, audit, responsive, Arabic RTL, mobile + wireframe.

## R1 screens (seed)
- UX-AUTH-001 Login (OIDC) — RTL, error/lockout states
- UX-TEN-001 Tenant switcher
- UX-ORG-001 Org tree
- UX-DIR-001 Directory (people search)
- UX-ADM-001 Role assignment

## Implemented screens (Next.js, `frontend/app/*`)
All screens are bilingual (EN/AR, RTL-mirrored via `useL`), share the tenant selector (`useTenant`),
show an accessible error banner (`role=alert`) and empty states, and hide actions the API would reject.

| Route | Screen | Purpose |
|---|---|---|
| `/my-work` | UX-INB-001 My Work | Approvals waiting on me, my tasks, notifications, inbox counts |
| `/requests` | UX-REQ-001 Requests | Catalog → dynamic form → submit/cancel, request timeline |
| `/tasks` | UX-TSK-001 Tasks | Create, assign, complete, verify tasks; overdue view |
| `/approvals` | UX-APR-006 Approval Workspace | Decide approvals (SoD enforced server-side) |
| `/correspondence` | UX-COR-001 Correspondence inbox | Register, route, reply |
| `/governance` | UX-GOV-001 Committees & meetings | Committees, members/terms, agenda, check-in, minutes, AI draft minutes |
| `/decisions` | UX-GOV-003 Decisions & execution | Decision lifecycle, actions, evidence, verification, overdue |
| `/policies` | UX-GOV-004 Policies & procedures | Versioning, review → legal → approval → publish, acknowledgement, procedures |
| `/calendar` | UX-CAL-001 Institutional calendar | 90-day view, events, `.ics` export |
| `/reports` | UX-RPT-001 Reports & dashboards | Executive KPIs, governance KPIs, reports, CSV export |
| `/intelligence` | UX-AI-001 Intelligence | RAG Q&A, search, institutional memory |
| `/directory`, `/roles`, `/audit` | UX-DIR-001, UX-ADM-001, UX-AUD-001 | People, role assignment, audit trail |
| `/records` | UX-REC-001 Records retention | Retention schedules, eligibility preview, legal holds, disposal batches (separate approver), manifest |
| `/settings` | UX-ADM-002 Settings | My account/permissions, notification preferences, SLA policies |

Dates: every screen formats dates through `fmtDate`/`fmtDay` (`frontend/lib/dates.ts`) in the viewer's
calendar preference: Gregorian, Hijri (Umm al-Qura, the official Saudi calendar), or both. Arabic users default to
both, English users to Gregorian; Settings (UX-ADM-002) changes it per device. Date inputs show the Hijri
equivalent underneath, and report CSV exports follow the preference (`?calendar=hijri|both` adds or substitutes
Umm al-Qura columns, computed on the server in Saudi time, UTC+3, configurable via `Calendar__UtcOffsetHours`).

RTL rules: `dir=rtl`, mirror navigation/actions, no hardcoded left/right, Arabic numerals optional, date Hijri/Gregorian toggle.
