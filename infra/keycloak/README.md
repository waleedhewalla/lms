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
