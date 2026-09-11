# OIDC integration proof (local Keycloak) — template for customer IdPs

Proven 2026-09-11: Keycloak 26 realm `edunexus` → API with
`Auth__Authority=http://127.0.0.1:8080/realms/edunexus` accepted the IdP token
(`GET /api/roles` → 200, permission + tenant-claim enforcement active).

## Run it
```powershell
docker compose -p edunexus --profile idp up -d keycloak
.\infra\keycloak\setup.ps1 -TenantId <tenant-guid>        # realm, client, mappers, user
.\infra\keycloak\roles-proof.ps1 -TenantId <tenant-guid>  # roles, claims, test token
$env:Auth__Authority="http://127.0.0.1:8080/realms/edunexus"
dotnet run --project backend/src/EduNexus.Api
```

## Claim contract (what the API requires)
| Claim | Source | Example |
|---|---|---|
| `iss` | IdP discovery (`Auth:Authority`) | `https://idp.local/realms/edunexus` |
| `aud` | must contain `Auth:Audience` | `edunexus-api` (Keycloak: audience mapper) |
| `tenant_id` | string claim | tenant GUID (Keycloak: hardcoded-claim mapper per tenant client, or user attribute) |
| `permission` | multivalued string claims | `role:read` (Keycloak: realm/client roles + role mapper, role names ARE permission strings) |

Recommended customer pattern: **one Keycloak client per tenant** (`edunexus-<slug>`)
with a hardcoded `tenant_id` claim; IdP roles named exactly like EduNexus permissions;
`Auth:Authority` + `Auth:Audience` per deployment. LDAP/SAML federations sit behind
Keycloak (or any OIDC broker) — no API changes needed.
Non-TLS IdP metadata is allowed outside Production only (`RequireHttpsMetadata` gate).
