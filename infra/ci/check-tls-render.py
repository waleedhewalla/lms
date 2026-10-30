"""CI gate: a chart rendered with tls.enabled=true must leave no plaintext path between the API and its data services.

Usage: python infra/ci/check-tls-render.py rendered.yaml [certManager|existingSecret]
Exit 0 = pass, 1 = fail with one line per problem.
"""
import sys
from pathlib import Path

import yaml

SERVICES = ["postgres", "redis", "rabbitmq", "minio", "opensearch", "api"]


def main() -> int:
    path = Path(sys.argv[1])
    mode = sys.argv[2] if len(sys.argv) > 2 else "certManager"
    docs = [d for d in yaml.safe_load_all(path.read_text(encoding="utf-8")) if d]
    errors: list[str] = []

    def by(kind: str, name: str):
        return next((d for d in docs if d.get("kind") == kind and d["metadata"]["name"] == name), None)

    def pod(kind: str, name: str):
        d = by(kind, name)
        if d is None:
            errors.append(f"{kind}/{name} is missing from the render")
            return {}
        return d["spec"]["template"]["spec"]

    def container(spec: dict, name: str):
        return next((c for c in spec.get("containers", []) if c["name"] == name), {})

    def secrets(spec: dict) -> dict:
        return {v["name"]: v["secret"] for v in spec.get("volumes", []) if "secret" in v}

    # --- certificates
    certs = [d for d in docs if d.get("kind") == "Certificate" and d["metadata"]["name"].startswith("edunexus-tls-")]
    if mode == "certManager":
        got = sorted(c["metadata"]["name"] for c in certs)
        want = sorted(f"edunexus-tls-{s}" for s in SERVICES)
        if got != want:
            errors.append(f"expected one Certificate per service {want}, got {got}")
        for c in certs:
            svc = c["metadata"]["name"].removeprefix("edunexus-tls-")
            if f"edunexus-{svc}" not in c["spec"].get("dnsNames", []):
                errors.append(f"{c['metadata']['name']} does not name edunexus-{svc}")
            if c["spec"]["secretName"] != c["metadata"]["name"]:
                errors.append(f"{c['metadata']['name']} writes to a different Secret than the workloads mount")
    elif certs or any(d.get("kind") == "Issuer" for d in docs):
        errors.append("existingSecret mode must not create cert-manager Issuers or Certificates")

    # --- postgres
    pg = pod("StatefulSet", "edunexus-postgres")
    pgc = container(pg, "postgres")
    if "-cssl=on" not in pgc.get("args", []):
        errors.append("postgres does not start with ssl=on")
    if "edunexus-tls-postgres" not in [s["secretName"] for s in secrets(pg).values()]:
        errors.append("postgres does not mount edunexus-tls-postgres")
    hba = by("ConfigMap", "edunexus-postgres-hba")
    rules = [l.split() for l in (hba or {}).get("data", {}).get("pg_hba.conf", "").splitlines() if l.strip() and not l.startswith("#")]
    if not any(r[0] == "hostssl" for r in rules):
        errors.append("pg_hba.conf has no hostssl rule")
    for r in rules:
        if r[0] == "host" and r[3] not in ("127.0.0.1/32", "::1/128"):
            errors.append(f"pg_hba.conf allows plaintext from the network: {' '.join(r)}")

    # --- redis
    rd = pod("StatefulSet", "edunexus-redis")
    args = [str(a) for a in container(rd, "redis").get("args", [])]
    if not ("--port" in args and args[args.index("--port") + 1] == "0" and "--tls-port" in args):
        errors.append("redis must have the plaintext port off (--port 0) and a --tls-port")

    # --- rabbitmq
    mq = pod("Deployment", "edunexus-rabbitmq")
    mq_ports = sorted(p["containerPort"] for p in container(mq, "rabbitmq").get("ports", []))
    if mq_ports != [5671, 15671]:
        errors.append(f"rabbitmq must expose only 5671 and 15671, got {mq_ports}")
    conf = (by("ConfigMap", "edunexus-rabbitmq-tls") or {}).get("data", {}).get("10-tls.conf", "")
    if "listeners.tcp = none" not in conf:
        errors.append("rabbitmq plain listener is not switched off")
    svc = by("Service", "edunexus-rabbitmq")
    if svc and sorted(p["port"] for p in svc["spec"]["ports"]) != [5671, 15671]:
        errors.append("the rabbitmq Service still exposes a plaintext port")

    # --- minio / opensearch
    if "--certs-dir" not in container(pod("StatefulSet", "edunexus-minio"), "minio").get("args", []):
        errors.append("minio is not given a certs directory")
    os_pod = pod("StatefulSet", "edunexus-opensearch")
    os_env = {e["name"]: e.get("value") for e in container(os_pod, "opensearch").get("env", [])}
    if os_env.get("network.host") != "127.0.0.1":
        errors.append("opensearch must listen on loopback only (the TLS proxy is the way in)")
    if "ports" in container(os_pod, "opensearch"):
        errors.append("opensearch itself must not publish a container port")
    if not container(os_pod, "tls-proxy"):
        errors.append("opensearch TLS proxy sidecar is missing")
    proxy = (by("ConfigMap", "edunexus-opensearch-proxy") or {}).get("data", {}).get("default.conf", "")
    if "listen 9200 ssl" not in proxy:
        errors.append("the opensearch proxy does not terminate TLS on 9200")

    # --- every data workload mounts its own certificate
    for name, kind, ctr in [("redis", "StatefulSet", "redis"), ("rabbitmq", "Deployment", "rabbitmq"),
                            ("minio", "StatefulSet", "minio"), ("opensearch", "StatefulSet", "opensearch")]:
        sp = pod(kind, f"edunexus-{name}")
        if f"edunexus-tls-{name}" not in [s["secretName"] for s in secrets(sp).values()]:
            errors.append(f"{name} does not mount edunexus-tls-{name}")

    # --- the API and the migration job
    for kind, name, ctr in [("Deployment", "edunexus-api", "api"), ("Job", "edunexus-migrate", "migrate")]:
        sp = pod(kind, name)
        env = {e["name"]: e.get("value") for e in container(sp, ctr).get("env", [])}
        if env.get("Tls__CaFile") != "/etc/edunexus/tls/ca.crt":
            errors.append(f"{name} has no Tls__CaFile")
        for s in secrets(sp).values():
            if s.get("items") != [{"key": "ca.crt", "path": "ca.crt"}]:
                errors.append(f"{name} must mount only ca.crt from its Secret, never a key")
        if kind != "Deployment":
            continue
        if env.get("RabbitMQ__Tls") != "true" or env.get("RabbitMQ__Port") != "5671":
            errors.append("api is not set to use RabbitMQ over TLS on 5671")
        if env.get("Storage__Secure") != "true":
            errors.append("api is not set to use MinIO over https")
        if not str(env.get("AI__OpenSearchUrl", "")).startswith("https://"):
            errors.append(f"api OpenSearch URL is not https: {env.get('AI__OpenSearchUrl')}")
        redis_cs = env.get("RateLimiting__Redis__ConnectionString")
        if redis_cs is not None and "ssl=true" not in redis_cs:
            errors.append("api Redis connection string does not set ssl=true")
        for k, v in env.items():
            if isinstance(v, str) and v.startswith("http://") and "edunexus-" in v:
                errors.append(f"api env {k} points at a plaintext in-cluster URL: {v}")

    for e in errors:
        print(f"FAIL: {e}")
    if not errors:
        print(f"PASS: {path} has no plaintext path to the data services ({mode}).")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
