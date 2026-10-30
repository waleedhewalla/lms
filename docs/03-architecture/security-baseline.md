# Security baseline (updated post-hardening)

## Implemented
- Security headers on all responses: nosniff, DENY framing, no-referrer, locked permissions-policy.
- JWT auth (OIDC or dev HS256); Production refuses dev defaults (startup gate, verified).
- Permission checks on every endpoint + tenant-claim match; 401/403 semantics tested.
- RLS (FORCE) on all tenant tables; 36 policies verified.
- Pre-commit secret hook; secrets-management doc; prod secrets git-ignored.

## Hardening 2026-10-06 (v1.3.x)
- Dependencies: OpenTelemetry upgraded to 1.19.x (GHSA-4625-4j76-fww9, GHSA-g94r-2vxg-569j fixed); Next 15.5.27,
  postcss ≥ 8.5.29, source-map-js ≥ 1.2.2 via npm overrides. `dotnet list package --vulnerable` and
  `npm audit`: **0 findings**. CI now fails on any vulnerable NuGet package or high/critical npm advisory.
- SSRF: webhook targets are validated at registration (no loopback/private/link-local/CGNAT literals,
  no credentials, http/https only) and every outbound connection's resolved IP is checked again at connect
  time (DNS-rebinding safe); redirects are not followed. Opt-out for on-prem: `Integrations:AllowPrivateTargets=true`.
- Webhook signing secrets: random 256-bit secret when none is supplied (was a constant default).
- Object storage keys supplied by clients must sit under the tenant's prefix (`{tenantId}/…`) with no traversal.
- Web: per-request Content-Security-Policy (connect-src limited to the configured API and IdP,
  frame-ancestors 'none', object-src 'none', form-action limited to self + IdP) plus nosniff, DENY, no-referrer.
- CORS restricted to configured origins; production refuses dev auth; non-root containers.
- Rate limiting in the API (2026-10-07): fixed window per user+tenant (per IP when anonymous), default 600/min,
  plus 60/min for AI, search, exports and token minting; 429 + `Retry-After`. Load tests run with it on: 0% errors.
- npm advisories published after 2026-10-06 (caught by the CI gate on 2026-10-25): Next.js 15.5.28 fixes
  GHSA-2464-fhr4-w9xw (critical, unauthenticated RCE via image optimization with SVG) and a Server Actions DoS;
  sharp ≥ 0.35.5 via override; unused `puppeteer` dev dependency removed (zod/chromium-bidi chain).
- Records disposal (Wave H): separation of duties (the proposer cannot decide), legal holds re-checked at execution,
  disposal register kept forever. Finding: the MinIO .NET SDK's remove/stat calls returned without error against an
  unreachable server, so deletions are now confirmed independently (a signed GET must return 404) before a record is
  reported destroyed. Other SDK calls (e.g. EnsureBucket) may hide failures the same way; worth a follow-up.
- `X-Forwarded-*` trusted only from private-network proxies (configurable), closing IP spoofing that the
  earlier "trust everyone" setting allowed. `/metrics` answers only loopback/private callers.

## Vulnerability audit 2026-09-11 (superseded)
- `dotnet list package --vulnerable`: 1 moderate — OpenTelemetry.Exporter.OpenTelemetryProtocol
  (GHSA-4625-4j76-fww9, no fixed release upstream; OTLP endpoint unconfigured by default).
  ACCEPTED RISK, re-check on each upgrade.
- `npm audit`: Next.js 15.1.6 criticals → upgraded to 15.5.25, build green.
  Remaining: postcss ≤8.5.22 high (build-time only, no runtime exposure). ACCEPTED, re-check on upgrade.

- Compliance mapping to NCA ECC-2:2024, CCC-2:2024 and PDPL, with gaps G1–G6 and a 72-hour breach runbook: `NCA-PDPL-MAPPING.md`.
  MFA (TOTP) is now enforced by default in the Keycloak realm.

## Still open (needs customer env / specialists)
- Human pen-test against the scope in `PENTEST-SCOPE.md`.
- Customer IdP wiring (turnkey Keycloak realm: `infra/keycloak/realm-edunexus.json`); Vault/SOPS (ADR-002);
  TLS termination at the edge per site (in-cluster TLS is `tls.enabled`); image signing for the air-gap bundle.
