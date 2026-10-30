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
| `ForwardedHeaders__KnownNetworks__0..n` | CIDRs of proxies whose `X-Forwarded-For` is trusted; default loopback + private ranges |
| `Metrics__AllowExternal` | `/metrics` is served only to loopback/private callers unless `true` |
| `RabbitMQ__Host/Port/Username/Password` | message broker |
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
