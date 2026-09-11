# EduNexus OS V2

Institutional operating system for universities: correspondence, workflow, governance, intelligence, AI.

## V2 Document Family (source of truth)

- `docs/01-master-blueprint` — Product vision, business blueprint, solution architecture
- `docs/02-frs` — Functional Requirements Specification (FR-xxx, Given/When/Then)
- `docs/03-architecture` — Technical/solution architecture + ADRs
- `docs/04-ux` — UX & screen specs (UX-xxx, RTL, states)
- `docs/05-processes` — Business process & workflow catalogue (BP-xxx)
- `docs/06-data` — Data dictionary & ERD
- `docs/07-api` — API & integration spec (OpenAPI)
- `docs/08-analytics` — Reports & analytics catalogue (RPT-xxx)
- `docs/09-ai` — AI specification (AI-01..09 + governance)
- `docs/10-roadmap` — Implementation roadmap (R1..R5)
- `docs/11-traceability` — Requirements Traceability Matrix (BR → … → TC)

Traceability chain:

```text
BR → Capability → BP → UC → FR → WF → UX → Entity → API → Event → RPT → TC
```

## Solution layout

```text
backend/  ASP.NET Core (.NET 9) — src/EduNexus.Api, src/EduNexus.Foundation
frontend/ Next.js + TypeScript (React, RTL-ready)
infra/    docker-compose (Postgres, Redis, OpenSearch, RabbitMQ, MinIO) + k8s
docs/     V2 family 01-11
```

## Tech (V2 recommendation)

Web: React+TS+Next.js · Mobile: PWA first · Backend: ASP.NET Core · DB: PostgreSQL
Cache: Redis · Search: OpenSearch · Queue: RabbitMQ · Objects: S3-compat
API: REST/OpenAPI + events · Auth: OIDC/OAuth2 · Deploy: Docker/K8s
Obs: OpenTelemetry + Prometheus + Grafana

## Quickstart (Release 1 — Foundation)

```powershell
$env:PATH="C:\Users\admin\.dotnet;"+$env:PATH
docker compose -f infra/docker-compose.yml up -d postgres redis rabbitmq minio opensearch
dotnet run --project backend/src/EduNexus.Api
cd frontend; npm install; npm run dev
```

Foundation scope (M1–M3): Tenant, Organization/Campus/Unit, Identity (OIDC),
Roles/Permissions/Authority, Directory (Person/Employee/Student), Security, Audit, Config.

See `docs/10-roadmap` for R1..R5 and `docs/11-traceability` for BR→TC chain.
