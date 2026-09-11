# Secrets management (R1 current → target)

## Current (R1, dev/staging)
- Connection strings and `Auth:*` come from `appsettings.json` < env vars (`EDUNEXUS_CONNECTION`) < `appsettings.{env}.json`.
- `appsettings.Production.json`, `*.pem`, `*.key`, `secrets/` are git-ignored; the pre-commit hook blocks them.
- Production refuses to boot without `Auth:Authority` and with `EnableDevToken=true` (see `Program.cs` gate).

## Rules
- No real secret in git, chat logs, or docs. Ever.
- Per-environment values live on the host (systemd EnvironmentFile / K8s SealedSecrets / Vault agent), not in the repo.
- Rotate `DevSigningKey`-class material out of the repo before any customer demo (it is a placeholder).
- Exception: `infra/keycloak/*.ps1` carry local-proof bootstrap credentials
  (`admin`/`tester123`, overridable via `KC_ADMIN_USER`/`KC_ADMIN_PASSWORD`/`KC_TEST_PASSWORD`).
  They address only the throwaway local Keycloak; the pre-commit hook exempts that path.
  Customer IdPs always use customer-managed credentials via config, never these scripts' defaults.

## Target (ADR-002, Phase 1)
Vault (or SOPS + age for air-gap) as the single source; K8s external-secrets sync;
short-lived DB credentials via dynamic secrets; rotation runbook + audit.
