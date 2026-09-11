# 11 — Requirements Traceability Matrix

Chain: `BR → Capability → BP → UC → FR → WF → UX → Entity → API → Event → RPT → TC`

## Example (must stay buildable)
BR-023 "Reduce approval delays" → C10 Approval → BP-FIN-004 Purchase Approval → UC-APR-012 Approve Request → FR-APR-034 → WF-FIN-004 → UX-APR-006 Approval Workspace → Request+Approval+Task+SLA → POST /approvals/{id}/approve → ApprovalCompleted → RPT-OPS-011 Approval Aging → TC-APR-034.

## Matrix (CSV)
See `matrix.csv`. Every FR added must append a row. CI check (future): fail if FR without TC or API without audit.
