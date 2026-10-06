#!/usr/bin/env bash
# Verifies an imported realm-edunexus.json issues the tokens the API expects.
# Usage: verify-realm.sh [keycloak-url] [admin-user] [admin-password]
# Creates a throwaway user in group edunexus-staff, asks Keycloak for the access token edunexus-web would
# issue for them, and asserts audience, tenant_id, person_id and permission claims. Deletes the user after.
set -euo pipefail
KC=${1:-http://127.0.0.1:8080}; ADMIN=${2:-admin}; PASS=${3:-admin}
TENANT=11111111-2222-3333-4444-555555555555; PERSON=99999999-8888-7777-6666-555555555555
USER=verify-$RANDOM; UPASS="Verify-Passw0rd-$RANDOM"

tok=$(curl -sf "$KC/realms/master/protocol/openid-connect/token" -d grant_type=password -d client_id=admin-cli \
  --data-urlencode "username=$ADMIN" --data-urlencode "password=$PASS" | python3 -c 'import json,sys;print(json.load(sys.stdin)["access_token"])')
H=(-H "Authorization: Bearer $tok" -H "Content-Type: application/json")

curl -sf "${H[@]}" "$KC/admin/realms/edunexus/users" -d "{\"username\":\"$USER\",\"enabled\":true,\"email\":\"$USER@example.test\",\"emailVerified\":true,
  \"firstName\":\"Verify\",\"lastName\":\"Realm\",\"attributes\":{\"tenant_id\":[\"$TENANT\"],\"person_id\":[\"$PERSON\"]},
  \"credentials\":[{\"type\":\"password\",\"value\":\"$UPASS\",\"temporary\":false}],\"groups\":[\"/edunexus-staff\"]}"
uid=$(curl -sf "${H[@]}" "$KC/admin/realms/edunexus/users?username=$USER&exact=true" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')
trap 'curl -sf -X DELETE "${H[@]}" "$KC/admin/realms/edunexus/users/$uid" >/dev/null || true' EXIT

# The web client must not allow password grants; PKCE (S256) is enforced.
code=$(curl -s -o /dev/null -w '%{http_code}' "$KC/realms/edunexus/protocol/openid-connect/token" -d grant_type=password \
  -d client_id=edunexus-web --data-urlencode "username=$USER" --data-urlencode "password=$UPASS")
[ "$code" = "400" ] || [ "$code" = "401" ] || { echo "edunexus-web accepted a password grant ($code)"; exit 1; }

# The token edunexus-web would issue for this user (Keycloak's evaluate-scopes endpoint, admin only).
cid=$(curl -sf "${H[@]}" "$KC/admin/realms/edunexus/clients?clientId=edunexus-web" | python3 -c 'import json,sys;print(json.load(sys.stdin)[0]["id"])')
access=$(curl -sf "${H[@]}" "$KC/admin/realms/edunexus/clients/$cid/evaluate-scopes/generate-example-access-token?scope=openid&userId=$uid" | python3 -c 'import json,sys;print(json.dumps(json.load(sys.stdin)))')
python3 - "$access" "$TENANT" "$PERSON" <<'PY'
import json, sys
payload, tenant, person = sys.argv[1:4]
c = json.loads(payload)
aud = c.get("aud"); aud = aud if isinstance(aud, list) else [aud]
assert "edunexus-api" in aud, f"aud={aud}"
assert c.get("tenant_id") == tenant, c.get("tenant_id")
assert c.get("person_id") == person, c.get("person_id")
perms = c.get("permission"); assert isinstance(perms, list) and "request:create" in perms and "tenant:create" not in perms, perms
print(f"OK: aud={aud} tenant_id={c['tenant_id']} permissions={len(perms)}")
PY
