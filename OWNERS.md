# EduNexus OS V2 — Document & Code Ownership

One owner per artifact. Owners approve changes to their area; cross-cutting changes need architect sign-off.

| Area | Path | Owner | Backup |
|---|---|---|---|
| 01 Master Blueprint | docs/01-master-blueprint | Architect | PO |
| 02 FRS | docs/02-frs | PO | Architect |
| 03 Architecture + ADRs | docs/03-architecture | Architect | Backend lead |
| 04 UX | docs/04-ux, frontend/ | Frontend lead | PO |
| 05 Processes | docs/05-processes | PO | Architect |
| 06 Data | docs/06-data, backend/*/Migrations | Backend lead | Architect |
| 07 API/Integrations | docs/07-api, backend/src/EduNexus.Api | Backend lead | Architect |
| 08 Analytics | docs/08-analytics | Data lead | PO |
| 09 AI | docs/09-ai | AI lead | Architect |
| 10 Roadmap | docs/10-roadmap | Architect | PO |
| 11 Traceability | docs/11-traceability | PO | Architect |
| Backend foundation | backend/src/EduNexus.Foundation, backend/src/EduNexus.Infrastructure | Backend lead | Architect |
| Infra/CI/CD | infra/, .github/ | DevOps lead | Backend lead |
| Security | Auth, secrets, RLS, policies | Security champ | Architect |

Rules:
- Every PR touching `docs/**` needs the area owner as reviewer.
- Stack changes require an ADR under `docs/03-architecture/adr/` before code.
- `matrix.csv` (11) must be updated in the same PR as any FR/API change — enforced by CI (`infra/ci/check-traceability.py`).
