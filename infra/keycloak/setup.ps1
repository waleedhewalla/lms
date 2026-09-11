# Keycloak local IdP proof for EduNexus OIDC (also serves as customer runbook template).
# Requires edunexus-keycloak on :8080 (admin/admin). Idempotent-ish: re-runnable.
# Usage: .\setup.ps1 -TenantId <guid>  (tenant_id claim stamped on user 'tester')
param([string]$TenantId = "11111111-1111-1111-1111-111111111111")
$ErrorActionPreference = "Stop"
$KC = "http://127.0.0.1:8080"
# Local-proof credentials only (see secrets-management.md). Override via env for shared hosts.
$AdminUser = $env:KC_ADMIN_USER; if (-not $AdminUser) { $AdminUser = "admin" }
$AdminPass = $env:KC_ADMIN_PASSWORD; if (-not $AdminPass) { $AdminPass = "admin" }
$TestPass = $env:KC_TEST_PASSWORD; if (-not $TestPass) { $TestPass = "tester123" }

$adminTok = (Invoke-RestMethod -Uri "$KC/realms/master/protocol/openid-connect/token" -Method Post -Body @{
  client_id = "admin-cli"; username = $AdminUser; password = $AdminPass; grant_type = "password"
}).access_token
$AH = @{ Authorization = "Bearer $adminTok" }
$JH = @{ Authorization = "Bearer $adminTok"; "Content-Type" = "application/json" }

try { Invoke-RestMethod -Uri "$KC/admin/realms/edunexus" -Headers $AH | Out-Null; $realmExists = $true }
catch { $realmExists = $false }
if (-not $realmExists) {
  Invoke-RestMethod -Uri "$KC/admin/realms" -Method Post -Headers $JH -Body '{"realm":"edunexus","enabled":true}' | Out-Null
  "realm created"
} else { "realm exists" }

$clients = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients?clientId=edunexus-api" -Headers $AH
if (-not $clients) {
  Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients" -Method Post -Headers $JH -Body (@{
    clientId = "edunexus-api"; publicClient = $true; directAccessGrantsEnabled = $true
    standardFlowEnabled = $true; attributes = @{}
  } | ConvertTo-Json) | Out-Null
  "client created"
} else { "client exists" }
$clientUuid = (Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients?clientId=edunexus-api" -Headers $AH)[0].id

function Add-Mapper($name, $body) {
  $existing = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients/$clientUuid/protocol-mappers/models" -Headers $AH
  if ($existing | Where-Object { $_.name -eq $name }) { "$name mapper exists"; return }
  Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients/$clientUuid/protocol-mappers/models" -Method Post -Headers $JH -Body ($body | ConvertTo-Json -Depth 5) | Out-Null
  "$name mapper created"
}
Add-Mapper "tenant-id" @{ name = "tenant-id"; protocol = "openid-connect"; protocolMapper = "oidc-usermodel-attribute-mapper"; consentRequired = $false; config = @{ "userinfo.token.claim" = "true"; "user.attribute" = "tenant_id"; "id.token.claim" = "true"; "access.token.claim" = "true"; "claim.name" = "tenant_id"; "jsonType.label" = "String" } }
Add-Mapper "permissions" @{ name = "permissions"; protocol = "openid-connect"; protocolMapper = "oidc-usermodel-attribute-mapper"; consentRequired = $false; config = @{ "userinfo.token.claim" = "true"; "user.attribute" = "permission"; "id.token.claim" = "true"; "access.token.claim" = "true"; "claim.name" = "permission"; "jsonType.label" = "String"; "multivalued" = "true" } }
Add-Mapper "api-audience" @{ name = "api-audience"; protocol = "openid-connect"; protocolMapper = "oidc-audience-mapper"; consentRequired = $false; config = @{ "included.client.audience" = "edunexus-api"; "id.token.claim" = "false"; "access.token.claim" = "true" } }

$users = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/users?username=tester" -Headers $AH
if (-not $users) {
  Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/users" -Method Post -Headers $JH -Body (@{
    username = "tester"; enabled = $true; emailVerified = $true; email = "tester@local"
    firstName = "Test"; lastName = "Er"; requiredActions = @()
    attributes = @{ tenant_id = @($TenantId); permission = @("role:read", "tenant:read") }
  } | ConvertTo-Json -Depth 4) | Out-Null
  $userId = (Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/users?username=tester" -Headers $AH)[0].id
  Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/users/$userId/reset-password" -Method Put -Headers $JH -Body (@{ type = "password"; value = $TestPass; temporary = $false } | ConvertTo-Json) | Out-Null
  "user created"
} else { "user exists" }
"SETUP_DONE"
