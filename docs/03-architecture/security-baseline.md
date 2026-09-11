# Security baseline (updated post-hardening)

## Implemented
- Security headers on all responses: nosniff, DENY framing, no-referrer, locked permissions-policy.
- JWT auth (OIDC or dev HS256); Production refuses dev defaults (startup gate, verified).
- Permission checks on every endpoint + tenant-claim match; 401/403 semantics tested.
- RLS (FORCE) on all tenant tables; 36 policies verified.
- Pre-commit secret hook; secrets-management doc; prod secrets git-ignored.

## Vulnerability audit 2026-09-11
- `dotnet list package --vulnerable`: 1 moderate — OpenTelemetry.Exporter.OpenTelemetryProtocol
  (GHSA-4625-4j76-fww9, no fixed release upstream; OTLP endpoint unconfigured by default).
  ACCEPTED RISK, re-check on each upgrade.
- `npm audit`: Next.js 15.1.6 criticals → upgraded to 15.5.25, build green.
  Remaining: postcss ≤8.5.22 high (build-time only, no runtime exposure). ACCEPTED, re-check on upgrade.

## Still open (needs customer env / specialists)
- Real pen-test; customer IdP wiring (Keycloak proof in progress); Vault/SOPS (ADR-002);
  TLS termination config per site; image signing for air-gap bundle.
