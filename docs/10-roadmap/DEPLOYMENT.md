# Deployment guide

EduNexus ships as two container images plus standard infrastructure:

| Component | Image | Port | Notes |
|---|---|---|---|
| API | `edunexus-api` (`backend/Dockerfile`) | 8080 | Runs as non-root; `/health`, `/metrics`; `--migrate-only` applies EF migrations and exits |
| Web | `edunexus-web` (`frontend/Dockerfile`) | 8000 | Next.js standalone; non-root (uid 1001); configured at runtime, one image for every site |
| Data | PostgreSQL 16, RabbitMQ 3, MinIO, OpenSearch 2 | | Redis, Prometheus, Grafana, Tempo optional |

Release images are published to `ghcr.io/<owner>/edunexus-api` and `ghcr.io/<owner>/edunexus-web`
by `.github/workflows/release.yml` when a `vX.Y.Z` tag is pushed. Every pull request builds both
images and lints/renders the Helm chart (`deploy-artifacts` CI job).

## 1. Identity provider (required for production)

The API refuses to start with `ASPNETCORE_ENVIRONMENT=Production` unless `Auth:Authority` is set and
`Auth:EnableDevToken=false`. Configure the IdP per `infra/keycloak/README.md` (claims `tenant_id`,
`permission`, optional `person_id`, `email`). **Keycloak:** import `infra/keycloak/realm-edunexus.json`
(set `EDUNEXUS_WEB_URL` to the web origin); it contains everything below plus permission groups.
Other IdPs: create a **public** client for the web app:

- Client ID `edunexus-web`, standard flow (Authorization Code) on, PKCE method `S256`, no client secret.
- Valid redirect URI `https://<web-host>/auth/callback`; post-logout redirect `https://<web-host>/`.
- Web origins `https://<web-host>` (lets the browser call the token endpoint).
- Access tokens must carry audience `edunexus-api` (Keycloak: audience mapper) and the claims above.

## 2. Runtime configuration

### API (environment variables)
| Variable | Purpose |
|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` on real sites |
| `EDUNEXUS_CONNECTION` | Npgsql connection string |
| `Auth__Authority`, `Auth__Audience` | OIDC issuer and audience |
| `Auth__EnableDevToken` | must be `false` in production |
| `Cors__AllowedOrigins` | browser origin(s) of the web app, comma-separated. Unset outside Development = no cross-origin access |
| `RateLimiting__PermitsPerMinute` | per signed-in user (per IP when anonymous); default 600. `RateLimiting__Enabled=false` turns limiting off |
| `RateLimiting__ExpensivePermitsPerMinute` | extra budget for AI, search, exports and token minting; default 60 |
| `RateLimiting__Redis__ConnectionString` | StackExchange.Redis connection string. When set, all API replicas count requests in Redis and share one budget; if Redis is unreachable the API keeps serving and counts per replica until it returns (a Warning is logged). Unset: each replica counts for itself, so with N replicas the effective limit is N times the number above. The Helm chart sets it to its own Redis by default (`api.rateLimiting.redis`). The limit is not a security boundary against a determined attacker: it is protection against runaway clients, and the ingress should add its own limit for anonymous traffic |
| `Connectors__Enabled` | `true` turns on read-only links to a learning or student-information system (Moodle, Canvas, Sakai, Banner) and a course-list import. Off by default. The Helm chart sets it with `api.connectors.enabled` |
| `Connectors__SecretKey` | 32 random bytes as base64 (`openssl rand -base64 32`): seals the credentials stored for those systems (AES-256-GCM). Required to save a live connection; a file import needs none. Keep the same value on every API replica and back it up: if it is lost or changed, stored credentials cannot be opened and the connections must be made again. The chart reads it from the `connectors-key` entry of the api Secret |
| `Integrations__AllowPrivateTargets` | `true` lets webhooks and connectors reach servers on internal addresses (a campus Moodle on 10.x). Default false: loopback, private, link-local and cloud-metadata addresses are refused at registration and at connection time |
| `CalendarFeed__TokenDays` | how long a personal calendar subscription link stays valid before the person must make a new one; default 90, allowed 1 to 365 |
| `ForwardedHeaders__KnownNetworks__0..n` | CIDRs of proxies whose `X-Forwarded-For` is trusted; default loopback + private ranges |
| `EDUNEXUS_OFFLINE_DATA` (web container) | `true` lets My Work be read and acted on without a connection: a small copy (counts, what is waiting for the person to approve, their open tasks; no letter text, notification bodies or tokens) is kept in the browser on that device, and approvals or task completions made offline wait until the person reviews and sends them. Off by default; nothing is stored on devices when it is off. Turn it on only where devices are managed and disk-encrypted. The copy is removed on sign-out, when another person signs in, after 7 days, and by a "Clear offline copy" button |
| `Metrics__AllowExternal` | `/metrics` is served only to loopback/private callers unless `true` |
| `RabbitMQ__Host/Port/Username/Password` | message broker |
| `RabbitMQ__Tls`, `RabbitMQ__TlsServerName` | connect to the broker over TLS (port 5671 on the chart); the certificate must carry `TlsServerName`, default `RabbitMQ__Host`. Checked against `Tls__CaFile` when set |
| `Tls__CaFile` | path of the site CA certificate (PEM). When set, the API checks the certificates of Postgres (`SSL Mode=VerifyFull`, unless the connection string chooses otherwise), RabbitMQ, MinIO (`Storage__Secure=true`), OpenSearch (https URL) and Redis (`ssl=true`) against that CA and the host name, and ignores the system roots for them. The Helm chart sets it with `tls.enabled`. Unset: nothing changes |
| `AI__OpenSearchUser`, `AI__OpenSearchPassword` | basic-auth login for an OpenSearch you secure yourself (optional; the chart's OpenSearch uses no login) |
| `Storage__Endpoint/AccessKey/SecretKey/Bucket/Secure` | S3-compatible object storage |
| `AI__OpenSearchUrl` | search / RAG index |
| `AI__Provider`, `AI__BaseUrl`, `AI__Model`, `AI__ApiKey` | `Echo` (default, no external calls) or `OpenAI` (any OpenAI-compatible endpoint, e.g. on-prem inference) |

### Web (environment variables, read per request by `/runtime-config.js`)
| Variable | Purpose |
|---|---|
| `EDUNEXUS_API_BASE` | public URL of the API as the browser sees it |
| `EDUNEXUS_OIDC_AUTHORITY` | IdP issuer; when set the UI shows **Sign in** instead of the dev token form |
| `EDUNEXUS_OIDC_CLIENT_ID` | public client ID (default `edunexus-web`) |
| `EDUNEXUS_GRAFANA_URL` | optional operations link in the header |

## 3a. Single host (Docker Compose)

```bash
cp infra/.env.example infra/.env        # fill in every value
docker compose -f infra/docker-compose.yml -f infra/docker-compose.app.yml --env-file infra/.env up -d --build
```

`migrate` runs once and must succeed before `api` starts. Web: `http://<host>:8000`, API: `http://<host>:5238`.
Put a TLS reverse proxy (nginx, Caddy, Traefik) in front of both and use the HTTPS URLs in `infra/.env`.
Without `infra/.env` the same command starts a local demo with dev-token sign-in.

## 3b. Kubernetes (Helm, including air-gapped sites)

Follow `infra/helm/edunexus/AIRGAP.md`: create the namespace and the `edunexus-postgres`,
`edunexus-minio` and `edunexus-api` secrets, then:

```bash
helm install edunexus infra/helm/edunexus -n edunexus -f site-values.yaml
```

Minimal `site-values.yaml`:

```yaml
registry: registry.local:5000/
api:
  auth: { authority: https://idp.example.edu/realms/edunexus, audience: edunexus-api }
web:
  publicUrl: https://edunexus.example.edu
  apiPublicUrl: https://api.edunexus.example.edu
ingress:
  enabled: true
  webHost: edunexus.example.edu
  apiHost: api.edunexus.example.edu
  tlsSecretName: edunexus-tls
```

The pre-install/pre-upgrade hook runs migrations before new API pods start.

## 3c. Encryption inside the cluster (optional)

Off by default. Turn it on before real data goes in:

```yaml
tls:
  enabled: true
  mode: certManager        # or existingSecret
networkPolicy:
  enabled: true            # optional: only the API and migration job may reach the data services
```

- **`certManager`**: needs cert-manager. The chart creates a CA and one certificate per service (renewed automatically, 90 days with renewal 15 days before). To sign with your own issuer instead, set `tls.issuerRef` (for example `{ name: corp-ca, kind: ClusterIssuer }`); the issuer must return its CA certificate as `ca.crt` in the Secret.
- **`existingSecret`**: for air-gapped sites or your own CA. Create `edunexus-tls-postgres`, `-redis`, `-rabbitmq`, `-minio`, `-opensearch` (each with `tls.crt`, `tls.key`, `ca.crt`) and `edunexus-tls-api` (only `ca.crt`). `infra/tls/gen-certs.sh` makes a CA and all of them and prints the `kubectl create secret` commands. Certificates must name `edunexus-<service>` (also with `.<namespace>`, `.svc` and `.svc.cluster.local`). To renew, replace the Secrets and restart the workloads.
- **What changes:** Postgres accepts only TLS from the network (`pg_hba` `hostssl`); Redis has no plaintext port; RabbitMQ listens on 5671 and 15671 only; MinIO serves https; OpenSearch sits behind a TLS proxy and listens on loopback only. The API and the migration job get the CA (never a key) and verify every connection against it and the service name; a wrong CA or name fails the connection instead of falling back.
- **Your Postgres connection secret** (`edunexus-api`, key `connection`) must use the host `edunexus-postgres`, which the certificate names. The API adds `SSL Mode=VerifyFull` and the CA itself.
- **Switching an existing install:** the data services restart with TLS, and API pods restart with the new settings; plan a short maintenance window. Rotating certificates restarts nothing by itself: the services read new files on their next restart.
- **Not covered:** the ingress to web and API hop and the identity provider (yours to secure); client certificates (servers prove who they are, clients still use passwords); OpenSearch's own security plugin (it is protected by network position and the TLS proxy; use `networkPolicy.enabled` too).

### 3d. Linking a learning or student-information system (optional)

Brings the **course catalogue** (code, title, term, dates, enrolment count, instructor names) from Moodle, Canvas, Sakai or Banner into EduNexus, so approvals and accreditation work can refer to the courses the institution really runs. EduNexus stays the governance layer beside those systems; it does not replace them.

- **What is read, and what is not:** courses only. No students, grades, submissions or enrolment lists; only a head count where the system reports one. Courses the system stops listing are marked inactive, never deleted.
- **Turn it on:** `api.connectors.enabled: true`, and add a `connectors-key` entry to the `edunexus-api` Secret (`openssl rand -base64 32`). Create a connection under `/api/connectors` with `integration:manage`.
- **What each system needs:** Moodle: a web-service token allowed to call `core_course_get_courses_by_field`. Canvas: an API token and the account id (option `accountId`, default `self`). Sakai: a service account as `username:password` (options `siteType`, `sessionCookie`). Banner: an Ethos API key (options `ethosAuthUrl`, `acceptVersion`). Any other system, or a site that prefers not to give a credential: create a **Csv** connection and upload an export (columns `code`, `title`; optional `externalId`, `term`, `startsOn`, `endsOn`, `enrolled`, `instructors`).
- **Network:** the API must be able to reach the system. On an internal address, set `Integrations__AllowPrivateTargets=true`.
- **Validate at the pilot:** the four clients are tested against sample answers shaped like each system's documented API, not against a live Moodle, Canvas, Sakai or Banner. A different version or configuration can answer differently; an answer in an unexpected shape fails with a clear message rather than importing guesses. Sakai and Banner calls differ most between versions and give no instructor names or counts.
- **Credentials:** sealed at rest, never returned by the API or written to logs, and used only from the API. Use an account with the least rights the system allows (read access to the course list).

## 4. First tenant

1. Sign in as a platform administrator (IdP user with `tenant:create`).
2. `POST /api/tenants`, then create organizations, people and roles from the Directory and Roles screens,
   or bulk-load people with `infra/scripts/sis_erp_sync.py` / the CSV directory import.
3. Map IdP users to tenant (`tenant_id` claim) and permissions (`permission` claim).

## 5. Operations

- Backups and point-in-time recovery: `infra/postgres/RUNBOOK.md`.
- Monitoring: Prometheus scrapes `/metrics`; alert rules in `infra/prometheus/alerts.yml`; Grafana dashboards in `infra/grafana`.
- Upgrade: pull/tag the new images, `helm upgrade` (or `docker compose ... up -d`); migrations run first.
- Rollback: redeploy the previous image tag; migrations are additive, restore from PITR if a schema rollback is needed.

## 6. Production checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production`, OIDC authority set, dev token disabled (the API enforces this).
- [ ] Every secret replaced (Postgres, MinIO, RabbitMQ, AI key); none committed.
- [ ] TLS on web, API and IdP; `Cors__AllowedOrigins` set to the web origin only.
- [ ] Backups scheduled and a restore drill done.
- [ ] Pen-test findings (GA gate 14) closed.
