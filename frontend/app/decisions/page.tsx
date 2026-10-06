"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Decision = { id: string; text: string; status: string; publishedAt: string; meetingId: string };
type Action = { id: string; assigneeId: string; description: string; status: string; dueAt: string };
type Evidence = { id: string; actionId: string; fileName: string; objectKey: string; at: string };
type Detail = { decision: Decision; actions: Action[]; evidence: Evidence[]; implementationPct: number | null };
type Overdue = { action: Action; decision: string; daysOverdue: number };

const NEXT: Record<string, string> = { Published: "Implemented", Implemented: "Verified", Verified: "Closed" };

export default function DecisionsPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [decisions, setDecisions] = useState<Decision[]>([]);
  const [overdue, setOverdue] = useState<Overdue[]>([]);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [assignee, setAssignee] = useState("");
  const [desc, setDesc] = useState("");
  const [due, setDue] = useState("");
  const [fileName, setFileName] = useState("");

  const load = () => attempt(setErr, async () => {
    setDecisions(await api<Decision[]>(`/api/decisions?tenantId=${tenantId}`));
    setOverdue(await api<Overdue[]>(`/api/decisions/overdue?tenantId=${tenantId}`));
  });
  const open = (id: string) => attempt(setErr, async () => setDetail(await api<Detail>(`/api/decisions/${id}?tenantId=${tenantId}`)));
  const post = (path: string, body: object) => attempt(setErr, async () => {
    await api(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
    if (detail) await open(detail.decision.id);
    await load();
  });

  return (
    <Page title={L("Decisions & Execution", "القرارات والتنفيذ")} code="UX-GOV-003" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      <div className="card-grid">
        <Card title={L("Decisions", "القرارات")}>
          <Table rows={decisions} empty={L("No decisions.", "لا قرارات.")} onRowClick={(d) => open(d.id)} columns={[
            { label: L("Decision", "القرار"), render: (d) => d.text },
            { label: L("Status", "الحالة"), render: (d) => <span className="badge">{d.status}</span> },
            { label: L("Published", "النشر"), render: (d) => fmtDate(d.publishedAt) },
          ]} />
        </Card>
        <Card title={L("Overdue actions", "إجراءات متأخرة")}>
          <Table rows={overdue} empty={L("Nothing overdue.", "لا شيء متأخر.")} columns={[
            { label: L("Action", "الإجراء"), render: (o) => o.action.description },
            { label: L("Decision", "القرار"), render: (o) => o.decision },
            { label: L("Days late", "أيام التأخير"), render: (o) => o.daysOverdue },
          ]} />
        </Card>
        {detail && (
          <Card title={detail.decision.text} wide>
            <p style={{ marginTop: 0 }}>
              {L("Status", "الحالة")}: <span className="badge">{detail.decision.status}</span> ·{" "}
              {L("Implementation", "نسبة التنفيذ")}: <strong>{detail.implementationPct ?? "–"}{detail.implementationPct != null ? "%" : ""}</strong>
              {NEXT[detail.decision.status] && (
                <button className="btn" style={{ marginInlineStart: "1rem" }}
                  onClick={() => post(`/api/decisions/${detail.decision.id}/transition`, { status: NEXT[detail.decision.status] })}>
                  {L("Move to", "نقل إلى")} {NEXT[detail.decision.status]}</button>)}
            </p>
            <Table rows={detail.actions} empty={L("No actions assigned.", "لا إجراءات.")} columns={[
              { label: L("Action", "الإجراء"), render: (a) => a.description },
              { label: L("Status", "الحالة"), render: (a) => <span className="badge">{a.status}</span> },
              { label: L("Due", "الاستحقاق"), render: (a) => fmtDate(a.dueAt) },
              { label: L("Evidence", "الأدلة"), render: (a) => detail.evidence.filter((e) => e.actionId === a.id).map((e) => e.fileName).join(", ") || "–" },
              { label: "", render: (a) => (
                <span style={{ display: "flex", gap: "0.25rem", flexWrap: "wrap" }}>
                  {(a.status === "Assigned" || a.status === "InProgress") && <>
                    <button className="btn btn-secondary" disabled={!fileName}
                      onClick={() => post(`/api/decision-actions/${a.id}/evidence`, { objectKey: `${tenantId}/evidence/${a.id}/${fileName}`, fileName, note: null })}>
                      {L("Attach evidence", "إرفاق دليل")}</button>
                    <button className="btn btn-secondary" onClick={() => post(`/api/decision-actions/${a.id}/advance`, { status: "Done" })}>{L("Mark done", "تم")}</button></>}
                  {a.status === "Done" && <button className="btn" onClick={() => post(`/api/decision-actions/${a.id}/verify`, {})}>{L("Verify", "تحقق")}</button>}
                </span>) },
            ]} />
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: "0.75rem", marginTop: "1rem" }}>
              <Field label={L("Evidence file name", "اسم ملف الدليل")}><input className="input" value={fileName} onChange={(e) => setFileName(e.target.value)} /></Field>
              <Field label={L("New action: assignee ID", "إجراء جديد: المكلّف")}><input className="input" value={assignee} onChange={(e) => setAssignee(e.target.value)} /></Field>
              <Field label={L("Description", "الوصف")}><input className="input" value={desc} onChange={(e) => setDesc(e.target.value)} /></Field>
              <Field label={L("Due", "الاستحقاق")}><input className="input" type="date" value={due} onChange={(e) => setDue(e.target.value)} /></Field>
            </div>
            <button className="btn" style={{ marginTop: "0.75rem" }} disabled={!assignee || !desc}
              onClick={() => post(`/api/decisions/${detail.decision.id}/actions`, { assigneeId: assignee, description: desc, dueAt: due ? new Date(due).toISOString() : null })}>
              {L("Assign action", "إسناد إجراء")}</button>
          </Card>
        )}
      </div>
    </Page>
  );
}
