"""CI gate: every traceability row must carry a functional requirement and a test case.

Usage: python infra/ci/check-traceability.py [path/to/matrix.csv]
Exit 0 = pass, 1 = fail with diagnostics.
"""
import csv
import sys
from pathlib import Path

VALID_STATUS = {"planned", "partial", "implemented"}
REQUIRED = ["BR", "Capability", "FR", "API", "TC", "Status"]


def main() -> int:
    path = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("docs/11-traceability/matrix.csv")
    if not path.exists():
        print(f"FAIL: matrix not found: {path}")
        return 1
    rows = list(csv.DictReader(path.read_text(encoding="utf-8").splitlines()))
    errors: list[str] = []
    seen_fr: dict[str, int] = {}
    for i, r in enumerate(rows, start=2):
        for col in REQUIRED:
            if not (r.get(col) or "").strip():
                errors.append(f"line {i}: empty required column '{col}' (FR={r.get('FR', '?')})")
        fr = (r.get("FR") or "").strip()
        if fr:
            seen_fr.setdefault(fr, []).append(i)
        status = (r.get("Status") or "").strip()
        if status and status not in VALID_STATUS:
            errors.append(f"line {i}: invalid Status '{status}' (want {sorted(VALID_STATUS)})")
    for fr, lines in seen_fr.items():
        if len(lines) > 1:
            errors.append(f"duplicate FR '{fr}' on lines {lines}")
    if errors:
        print(f"FAIL: {len(errors)} traceability violation(s):")
        for e in errors:
            print(f"  - {e}")
        return 1
    implemented = sum(1 for r in rows if (r.get("Status") or "").strip() == "implemented")
    print(f"PASS: {len(rows)} rows, {implemented} implemented, all carry FR + TC.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
