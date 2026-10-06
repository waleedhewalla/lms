"use client";
import { useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { Card, Kpi, Page, Table, attempt, useL, useTenant } from "../../components/Ui";

type Governance = {
  committees: number; meetingsUpcoming: number; meetingsConcluded: number; decisions: number; decisionActions: number;
  decisionActionsCompleted: number; decisionActionsOverdue: number; implementationRatePct: number | null;
  policiesPublished: number; policyAcknowledgementPct: number | null;
};
type Executive = {
  requests: { byStatus: { status: string; count: number }[]; createdLast30Days: number };
  approvals: { pending: number; overdue: number; slaCompliancePct30d: number | null };
  tasks: { open: number; overdue: number; breached: number };
  communicationsPublished30d: number;
  governance: Governance;
};
type Row = Record<string, string | number | boolean | null>;
const REPORTS = ["approval-bottlenecks", "workflow-cycle-time", "committees", "decisions", "policies"];
const pct = (v: number | null | undefined) => (v == null ? "–" : `${v}%`);

export default function ReportsPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [exec, setExec] = useState<Executive | null>(null);
  const [code, setCode] = useState(REPORTS[0]);
  const [rows, setRows] = useState<Row[]>([]);

  const load = () => attempt(setErr, async () => {
    setExec(await api<Executive>(`/api/dashboards/executive?tenantId=${tenantId}`));
    setRows(await api<Row[]>(`/api/reports/${code}?tenantId=${tenantId}`));
  });
  const runReport = (c: string) => attempt(setErr, async () => {
    setCode(c);
    setRows(await api<Row[]>(`/api/reports/${c}?tenantId=${tenantId}`));
  });
  const exportCsv = () => attempt(setErr, async () => {
    const res = await fetch(`${apiBase()}/api/reports/${code}/export?tenantId=${tenantId}`, { headers: authHeaders() });
    if (!res.ok) throw new Error(`export → ${res.status}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a");
    a.href = url; a.download = `${code}.csv`; a.click();
    URL.revokeObjectURL(url);
  });

  const g = exec?.governance;
  const columns = rows[0] ? Object.keys(rows[0]).map((k) => ({ label: k, render: (r: Row) => String(r[k] ?? "–") })) : [];

  return (
    <Page title={L("Reports & Dashboards", "التقارير ولوحات المؤشرات")} code="UX-RPT-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      <h3>{L("Operations", "العمليات")}</h3>
      <div className="card-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))" }}>
        <Kpi label={L("Pending approvals", "اعتمادات معلقة")} value={exec?.approvals.pending} />
        <Kpi label={L("Overdue approvals", "اعتمادات متأخرة")} value={exec?.approvals.overdue} />
        <Kpi label={L("SLA compliance (30d)", "الالتزام بالمدة (30 يومًا)")} value={pct(exec?.approvals.slaCompliancePct30d)} />
        <Kpi label={L("Open tasks", "مهام مفتوحة")} value={exec?.tasks.open} hint={exec ? `${exec.tasks.overdue} ${L("overdue", "متأخرة")}` : undefined} />
        <Kpi label={L("Requests (30d)", "طلبات (30 يومًا)")} value={exec?.requests.createdLast30Days} />
        <Kpi label={L("Announcements (30d)", "تعاميم (30 يومًا)")} value={exec?.communicationsPublished30d} />
      </div>
      <h3>{L("Governance", "الحوكمة")}</h3>
      <div className="card-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(170px, 1fr))" }}>
        <Kpi label={L("Committees", "اللجان")} value={g?.committees} />
        <Kpi label={L("Upcoming meetings", "اجتماعات قادمة")} value={g?.meetingsUpcoming} />
        <Kpi label={L("Decisions", "القرارات")} value={g?.decisions} />
        <Kpi label={L("Implementation rate", "نسبة التنفيذ")} value={pct(g?.implementationRatePct)} hint={g ? `${g.decisionActionsOverdue} ${L("actions overdue", "إجراءات متأخرة")}` : undefined} />
        <Kpi label={L("Published policies", "سياسات منشورة")} value={g?.policiesPublished} />
        <Kpi label={L("Policy acknowledgement", "الإقرار بالسياسات")} value={pct(g?.policyAcknowledgementPct)} />
      </div>
      <div style={{ marginTop: "1.5rem" }}>
        <Card title={L("Reports", "التقارير")} wide>
          <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", marginBottom: "1rem" }}>
            {REPORTS.map((r) => (
              <button key={r} className={r === code ? "btn" : "btn btn-secondary"} onClick={() => runReport(r)}>{r}</button>))}
            <button className="btn btn-secondary" onClick={exportCsv}>⬇ CSV</button>
          </div>
          <Table rows={rows} columns={columns} empty={L("No rows.", "لا بيانات.")} />
        </Card>
      </div>
    </Page>
  );
}
