# OIDC proof part 2: realm roles -> permission claims + hardcoded tenant claim.
# Usage: .\roles-proof.ps1 -TenantId <guid>
param([string]$TenantId = "4d526df0-f023-41f3-bdd9-f1b742118df6")
$ErrorActionPreference = "Stop"
$KC = "http://127.0.0.1:8080"
# Local-proof credentials only (see secrets-management.md). Override via env for shared hosts.
$AdminUser = $env:KC_ADMIN_USER; if (-not $AdminUser) { $AdminUser = "admin" }
$AdminPass = $env:KC_ADMIN_PASSWORD; if (-not $AdminPass) { $AdminPass = "admin" }
$TestPass = $env:KC_TEST_PASSWORD; if (-not $TestPass) { $TestPass = "tester123" }

$adminTok = (Invoke-RestMethod -Uri "$KC/realms/master/protocol/openid-connect/token" -Method Post -Body @{
  client_id = "admin-cli"; username = $AdminUser; password = $AdminPass; grant_type = "password"
}).access_token
$H = @{ Authorization = "Bearer $adminTok" }
$JH = @{ Authorization = "Bearer $adminTok"; "Content-Type" = "application/json" }

$client = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients?clientId=edunexus-api" -Headers $H
if ($client -is [array]) { $client = $client | Where-Object { $_.clientId -eq "edunexus-api" } | Select-Object -First 1 }
$cid = $client.id
"client=$cid"

foreach ($r in @("role:read", "tenant:read")) {
  $exists = $null
  try { $exists = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/roles/$r" -Headers $H } catch { }
  if (-not $exists) {
    Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/roles" -Method Post -Headers $JH -Body (@{ name = $r } | ConvertTo-Json) | Out-Null
    "$r created"
  } else { "$r exists" }
}

function Ensure-Mapper($name, $mapper, $cfg) {
  $existing = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients/$cid/protocol-mappers/models" -Headers $H
  if ($existing | Where-Object { $_.name -eq $name }) { "$name exists"; return }
  Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/clients/$cid/protocol-mappers/models" -Method Post -Headers $JH -Body (@{
    name = $name; protocol = "openid-connect"; protocolMapper = $mapper; consentRequired = $false; config = $cfg
  } | ConvertTo-Json -Depth 5) | Out-Null
  "$name created"
}
Ensure-Mapper "perm-from-roles" "oidc-usermodel-realm-role-mapper" @{
  "claim.name" = "permission"; "jsonType.label" = "String"; "multivalued" = "true"
  "userinfo.token.claim" = "true"; "id.token.claim" = "true"; "access.token.claim" = "true"
}
Ensure-Mapper "tenant-hardcoded" "oidc-hardcoded-claim-mapper" @{
  "claim.name" = "tenant_id"; "claim.value" = $TenantId; "jsonType.label" = "String"
  "userinfo.token.claim" = "true"; "id.token.claim" = "true"; "access.token.claim" = "true"
}

$users = Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/users?max=50" -Headers $H
$user = @($users | Where-Object { $_.username -eq "tester" })[0]
"user=$($user.id) $($user.username)"
$roles = @((Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/roles" -Headers $H) | Where-Object { $_.name -in @("role:read", "tenant:read") })
"roles=$($roles.name -join ',')"
Invoke-RestMethod -Uri "$KC/admin/realms/edunexus/users/$($user.id)/role-mappings/realm" -Method Post -Headers $JH -Body ($roles | ConvertTo-Json -Depth 5) | Out-Null
"roles-assigned"

$tok = (Invoke-RestMethod -Uri "$KC/realms/edunexus/protocol/openid-connect/token" -Method Post -Body @{
  client_id = "edunexus-api"; username = "tester"; password = $TestPass; grant_type = "password"
}).access_token
$pay = $tok.Split('.')[1]
while ($pay.Length % 4) { $pay += '=' }
$claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($pay.Replace('-', '+').Replace('_', '/'))) | ConvertFrom-Json
"claims: tenant_id=$($claims.tenant_id) permission=$($claims.permission -join ',') aud=$($claims.aud -join ',')"
Set-Content -Path "$env:TEMP\kctok.txt" -Value $tok
"PROOF_TOKEN_SAVED"
