# Air-gap install (private registry, no internet on cluster)
# 1. On a connected machine, mirror every image in values.yaml:
#      for img in postgres:16 redis:7 rabbitmq:3-management minio/minio \
#        opensearchproject/opensearch:2 prom/prometheus:v3.1.0 grafana/grafana:11.4.0 \
#        edunexus-api:1.1.0 edunexus-web:1.1.0; do
#        docker pull $img
#        docker tag $img registry.local:5000/$img
#        docker push registry.local:5000/$img
#      done
#    Build app images (verified 2026-09-11):
#      docker build -f backend/Dockerfile -t edunexus-api:1.1.0 backend/
#      docker build -f frontend/Dockerfile -t edunexus-web:1.1.0 frontend/
#      docker run --rm -p 5298:8080 -e ASPNETCORE_ENVIRONMENT=Development \
#        -e EDUNEXUS_CONNECTION="Host=host.docker.internal;Port=5433;..." edunexus-api:1.1.0
#      # /health → Healthy, /metrics live (smoke-proven)
# 2. Copy this chart + values to the site (usb/tar).
# 3. Create secrets (NEVER in values files):
#      kubectl -n edunexus create secret generic edunexus-postgres --from-literal=password=$(openssl rand -base64 24)
#      kubectl -n edunexus create secret generic edunexus-minio --from-literal=root-password=$(openssl rand -base64 24)
# 4. Install with site values:
#      helm install edunexus ./infra/helm/edunexus -n edunexus --create-namespace \
#        --set registry=registry.local:5000/ \
#        --set api.auth.authority=https://idp.local/realms/edunexus \
#        -f site-values.yaml
# 5. Run EF migrations as a Job (dotnet-ef container or init container), verify /health,
#    restore backup per infra/postgres/RUNBOOK.md if migrating.
