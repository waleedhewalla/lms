"""SIS/ERP Integration Delta Sync Pipeline (FEAT-06)

Synchronizes Student Information System (SIS) and Enterprise Resource Planning (ERP)
data dumps into EduNexus OS V2 API with dry-run support, delta field matching, and validation.

Usage:
    python infra/scripts/sis_erp_sync.py --api-url http://localhost:5000 --tenant-id <GUID> --token <JWT> --sis-file sis_data.csv --dry-run
"""
import argparse
import csv
import json
import sys
import urllib.request
import urllib.error

def sync_sis_people(api_url: str, tenant_id: str, token: str, csv_path: str, dry_run: bool) -> dict:
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "text/csv"
    }
    url = f"{api_url}/api/people/import?tenantId={tenant_id}&dryRun={'true' if dry_run else 'false'}"
    
    with open(csv_path, "rb") as f:
        data = f.read()
        
    req = urllib.request.Request(url, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        print(f"Error {e.code}: {e.read().decode('utf-8')}")
        sys.exit(1)

def main():
    parser = argparse.ArgumentParser(description="SIS/ERP Sync Pipeline")
    parser.add_argument("--api-url", default="http://localhost:5000", help="Base API URL")
    parser.add_argument("--tenant-id", required=True, help="Tenant GUID")
    parser.add_argument("--token", required=True, help="Bearer JWT Token")
    parser.add_argument("--sis-file", required=True, help="Path to SIS CSV export file")
    parser.add_argument("--dry-run", action="store_true", help="Perform validation without writing to DB")

    args = parser.parse_args()
    res = sync_sis_people(args.api_url, args.tenant_id, args.token, args.sis_file, args.dry_run)
    print(f"Sync complete (dry_run={args.dry_run}): {res}")

if __name__ == "__main__":
    main()
