# 06 — Data Dictionary & ERD

Per entity: definition, purpose, attributes (type, required, validation), relations, ownership, classification, retention, audit.

## R1 entities (implemented in `EduNexus.Foundation`)
Tenant, Organization, Campus, OrganizationalUnit, Person, Employee, Student,
Position, Role, Permission, RoleAssignment, AuthorityDelegation, AuditEvent, SecurityEvent.

Conventions: UUID PKs, `tenant_id` everywhere, `created_at/updated_at`, soft-delete (`deleted_at`) for directory entities, unique correspondence/request numbers later (R2).

ERD: see `erd.puml` (to add) — Tenant 1—* Organization 1—* Campus 1—* OrganizationalUnit; Person 1—0..1 Employee/Student; Role *—* Permission; RoleAssignment → Person+scope+expiry.
