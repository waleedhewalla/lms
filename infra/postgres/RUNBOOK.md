# Postgres backup & PITR runbook (on-prem reference)

## Architecture
- `wal_level=replica`, `archive_mode=on`, `archive_timeout=60`, archive to `/walarchive`
  (volumes `walarchive`, `pgbackup`; see `infra/docker-compose.yml`). RPO ≤ 60s + segment shipping.
- Nightly `pg_basebackup -D /backups/base -Ft -z -c fast` (cron/K8s CronJob in prod).
- Drill result 2026-09-11: base + WAL replay in scratch container recovered a post-backup
  marker row, 3 tenants, 8 RLS policies. Chain verified working.

## Nightly backup
```sh
docker exec edunexus-postgres pg_basebackup -U edunexus -D /backups/base-$(date +%F) -Ft -z -c fast
docker exec edunexus-postgres psql -U edunexus -d edunexus -c "SELECT pg_switch_wal();"
ls $(docker volume inspect edunexus_walarchive --format '{{.Mountpoint}}')  # segments present
```

## PITR restore (scratch verify or real failover)
```sh
docker volume create restore_data
docker run --rm -v pgbackup:/backups:ro -v restore_data:/data postgres:16 \
  bash -c 'tar xzf /backups/base-<date>/base.tar.gz -C /data && touch /data/recovery.signal'
# append (from infra/postgres/restore-snippet.conf):
#   restore_command = 'cp /walarchive/%f %p'
#   archive_mode = off
# optional point-in-time: recovery_target_time = '2026-09-11 17:00:00+00'
docker run -d --name restore -p 5434:5432 -v restore_data:/var/lib/postgresql/data \
  -v walarchive:/walarchive:ro postgres:16
# verify: marker/tables/row counts, then promote (DELETE recovery.signal + restart) or cut over.
```

## RTO/RPO targets (ratify in 01)
RPO ≤ 15 min (archive_timeout + shipping), RTO ≤ 1 h (base unpack + replay measured ~1 min
for current size; re-measure quarterly). Quarterly drill mandatory; log results here.
