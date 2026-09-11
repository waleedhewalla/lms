# ADR-001 — On-prem deployment topology

Status: accepted

## Context
Target customers are universities running private infrastructure (per program scope:
on-prem/private, no cloud-managed-service assumptions). Must support air-gap install,
customer-operated Postgres/backup, and a small ops team. Current dev topology is
single-host docker-compose (Postgres 16, Redis 7, RabbitMQ 3, MinIO, OpenSearch 2).

## Decision
- Production topology: **single K8s cluster on customer VMs** (k3s/RKE2 acceptable),
  one `edunexus` namespace; all stateful services run in-cluster from pinned images
  mirrored to the customer's private registry.
- Postgres: in-cluster StatefulSet with PITR (WAL-G to customer S3-compatible storage);
  RPO ≤ 15 min, RTO ≤ 1 h (NFRs to be ratified in 01).
- Redis/RabbitMQ/MinIO/OpenSearch: in-cluster, persistent volumes, resource limits set.
- No cloud-managed services in the default path; cloud variants (managed PG etc.) are
  documented options, not the reference.
- CI (`ci.yml`) runs on self-hosted runners with the same container versions as prod.

## Consequences
- Must ship: k8s manifests + Helm chart or kustomize, air-gap bundle (images + charts),
  backup/restore runbooks and drill, resource sizing guide per 1k/10k users.
- Compose file remains the **dev** topology only; prod divergences must be recorded here.
- Next ADRs: secrets management (002), IdP bridge matrix (003).
