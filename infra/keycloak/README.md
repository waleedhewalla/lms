# Customer IdP Integration Runbook (GATE-11)

This runbook documents the configuration patterns for integrating EduNexus OS V2 with customer Identity Providers (IdPs).

---

## 🔑 Claim Contract (What the API Requires)

| Claim | Type | Description / Value | Keycloak / IdP Mapper |
|---|---|---|---|
| `iss` | String | IdP Issuer URL (must match `Auth:Authority`) | `https://idp.customer.edu/realms/edunexus` |
| `aud` | String | Audience identifier | Must match `Auth:Audience` (e.g. `edunexus-api`) |
| `tenant_id` | String (GUID) | Tenant Isolation GUID | Protocol mapper set to tenant UUID |
| `permission` | Array of Strings | Granted permission strings | `role:read`, `person:create`, `document:create`, etc. |
| `person_id` | String (GUID), optional | The directory `Person` the user acts as (approvals, delegation) | User attribute mapper; if absent the API matches `email` to `Person.Email` |
| `email` | String | Fallback for resolving the acting person (must be unique in the tenant) | Standard `email` scope |

---

## ⚡ Turnkey Keycloak realm (recommended)

`realm-edunexus.json` is a complete, importable realm. It contains:

- **Client `edunexus-web`:**
  - a public client using Authorization Code + PKCE S256, with password grants disabled;
  - redirect, logout and web origins taken from `EDUNEXUS_WEB_URL`;
  - mappers for the audience (`EDUNEXUS_API_AUDIENCE`, default `edunexus-api`), `tenant_id`, `person_id` and `permission`.
- **Permission groups:** users get their permissions by joining one of these.

  | Group | Permissions |
  |---|---|
  | `edunexus-platform-admins` | Everything, including `tenant:create` |
  | `edunexus-tenant-admins` | Everything inside the tenant |
  | `edunexus-leaders` | Verify, analytics, policy authoring, audit |
  | `edunexus-secretaries` | Meetings, minutes, decisions, actions |
  | `edunexus-staff` | Requests, tasks, approvals, documents, governance read |
  | `edunexus-readonly` | Every `:read` permission |

- **Declared user profile:** `tenant_id` and `person_id` hold GUIDs that only admins can edit.
- **Security settings:**
  - brute-force lockout;
  - password policy: at least 12 characters, not the username or email, last 5 passwords can't be reused;
  - 15-minute access tokens, with refresh-token rotation.

**Import:**
- Compose: `docker compose -f infra/docker-compose.yml --profile idp up -d keycloak`. The realm is mounted and imported on first start.
- Elsewhere: `kc.sh start --import-realm` with the file in `/opt/keycloak/data/import/`.

**Onboard a user:**
1. Create the user and set `tenant_id`, plus `person_id` if the email isn't unique in the directory.
2. Add the user to one group.

**Verify:** `infra/keycloak/verify-realm.sh <keycloak-url> <admin> <password>` asserts the token claims. CI runs it on every PR.

**Proven 2026-10-06:**
- A browser signed in on Keycloak 26's own login page using PKCE S256.
- The web app received the token, and the API in OIDC mode accepted it: `/api/auth/me` returned the 31 staff permissions.
- The browser reported zero CSP violations.

`setup.ps1` is kept as the earlier imperative proof. Prefer the realm file.

## 🛠️ Customer Integration Patterns

### Pattern 1: Azure AD / Entra ID Integration
1. **Register Enterprise Application**: Create a app registration in Azure Portal (`EduNexus-Enterprise`).
2. **Add Optional Claims**: Configure `tenant_id` as custom claim and map Entra App Roles to `permission` array claims.
3. **Configure Keycloak Broker** (or Direct OIDC):
   - OpenID Connect v1.0 Provider endpoint: `https://login.microsoftonline.com/{tenant-id}/v2.0`
   - Client ID & Secret from Azure Portal.
4. **Set Helm Values**:
   ```yaml
   api:
     auth:
       authority: "https://login.microsoftonline.com/{customer-tenant-id}/v2.0"
       audience: "edunexus-api"
   ```

### Pattern 2: ADFS / SAML 2.0 Identity Federation
1. Configure SAML 2.0 Identity Provider in Keycloak (`Identity Providers -> SAML 2.0`).
2. Map SAML Assertions:
   - `http://schemas.microsoft.com/identity/claims/tenantid` → `tenant_id`
   - `http://schemas.xmlsoap.org/claims/Group` → `permission`
3. Import ADFS XML Metadata into Keycloak.

### Pattern 3: Direct OIDC (Okta / Auth0 / PingFederate)
1. Skip Keycloak broker layer.
2. Configure EduNexus API Helm values directly:
   ```yaml
   api:
     auth:
       authority: "https://customer.okta.com/oauth2/default"
       audience: "api://edunexus"
   ```

---

## 🚨 Security Baseline & Production Guidelines
- **TLS Requirement**: In Production (`ASPNETCORE_ENVIRONMENT=Production`), `RequireHttpsMetadata=true` is strictly enforced. Plain HTTP authority endpoints will fail startup.
- **Dev Token Gate**: `Auth:EnableDevToken` MUST be `false` in Production. The API will throw an exception on boot if `EnableDevToken=true` in Production.

---

## 🔍 Troubleshooting Checklist
- ❌ **401 Unauthorized**: Token issuer (`iss`) does not match `Auth:Authority`, or token is expired.
- ❌ **403 Forbidden**: `tenant_id` claim in JWT does not match request `tenantId` parameter (Cross-Tenant Guard), or `permission` array lacks required scope.
