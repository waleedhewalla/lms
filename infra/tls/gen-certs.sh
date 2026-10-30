#!/usr/bin/env bash
# Makes a private CA and one certificate per EduNexus service, for sites that cannot use cert-manager
# (tls.mode=existingSecret) or want to try TLS locally. Needs only openssl (and kubectl for the last step).
#
#   infra/tls/gen-certs.sh [output-dir] [namespace]      defaults: ./edunexus-tls  edunexus
#
# Prints the kubectl commands that create the Secrets the chart expects. Keep ca.key offline: it signs every service.
set -euo pipefail

out="${1:-edunexus-tls}"
ns="${2:-edunexus}"
days="${DAYS:-365}"
services=(postgres redis rabbitmq minio opensearch api)

[ -e "$out" ] && { echo "refusing to overwrite $out (move it or pick another directory)" >&2; exit 1; }
umask 077
mkdir -p "$out"

openssl req -x509 -newkey rsa:3072 -nodes -keyout "$out/ca.key" -out "$out/ca.crt" -days $((days * 5)) \
  -subj "/CN=edunexus-ca" -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" 2>/dev/null

for s in "${services[@]}"; do
  host="edunexus-$s"
  cat > "$out/$s.ext" <<EXT
basicConstraints=CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth,clientAuth
subjectAltName=DNS:$host,DNS:$host.$ns,DNS:$host.$ns.svc,DNS:$host.$ns.svc.cluster.local
EXT
  openssl req -newkey rsa:3072 -nodes -keyout "$out/$s.key" -out "$out/$s.csr" -subj "/CN=$host" 2>/dev/null
  openssl x509 -req -in "$out/$s.csr" -CA "$out/ca.crt" -CAkey "$out/ca.key" -CAcreateserial -out "$out/$s.crt" \
    -days "$days" -extfile "$out/$s.ext" 2>/dev/null
  rm -f "$out/$s.csr" "$out/$s.ext"
done

echo "Created CA and ${#services[@]} service certificates in $out (valid $days days). Create the Secrets:"
echo
for s in "${services[@]}"; do
  if [ "$s" = api ]; then
    echo "kubectl -n $ns create secret generic edunexus-tls-api --from-file=ca.crt=$out/ca.crt"
  else
    echo "kubectl -n $ns create secret generic edunexus-tls-$s --from-file=tls.crt=$out/$s.crt --from-file=tls.key=$out/$s.key --from-file=ca.crt=$out/ca.crt"
  fi
done
echo
echo "Then: helm upgrade --install edunexus infra/helm/edunexus --set tls.enabled=true --set tls.mode=existingSecret"
echo "Renew before expiry by running this again into a new directory and replacing the Secrets, then restart the workloads."
