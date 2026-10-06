"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Policy = { id: string; code: string; title: string; content: string; status: string; version: number; nextReviewAt: string | null };
type Version = { id: string; version: number; title: string; changeNote: string | null; createdAt: string };
type Procedure = { id: string; code: string; title: string; steps: string };
type Detail = { policy: Policy; versions: Version[]; acknowledgements: number; procedures: Procedure[] };

const FLOW: Record<string, string[]> = {
  Draft: ["Review"], Review: ["LegalReview", "Draft"], LegalReview: ["Approval", "Review"], Approval: ["Published", "Review"], Published: ["Review"],
};

export default function PoliciesPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [policies, setPolicies] = useState<Policy[]>([]);
  const [upcoming, setUpcoming] = useState<Policy[]>([]);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [code, setCode] = useState("");
  const [title, setTitle] = useState("");
  const [content, setContent] = useState("");
  const [revision, setRevision] = useState("");
  const [note, setNote] = useState("");
  const [review, setReview] = useState("");
  const [pCode, setPCode] = useState("");
  const [pTitle, setPTitle] = useState("");
  const [pSteps, setPSteps] = useState("");

  const load = () => attempt(setErr, async () => {
    setPolicies(await api<Policy[]>(`/api/policies?tenantId=${tenantId}`));
    setUpcoming(await api<Policy[]>(`/api/policies/reviews/upcoming?tenantId=${tenantId}&days=60`));
  });
  const open = (id: string) => attempt(setErr, async () => {
    const d = await api<Detail>(`/api/policies/${id}?tenantId=${tenantId}`);
    setDetail(d); setRevision(d.policy.content);
  });
  const post = (path: string, body: object) => attempt(setErr, async () => {
    await api(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
    await load();
    if (detail) await open(detail.policy.id);
  });
  const create = () => attempt(setErr, async () => {
    const p = await api<Policy>("/api/policies", { method: "POST", body: JSON.stringify({ tenantId, code, title, content }) });
    setCode(""); setTitle(""); setContent("");
    await load(); await open(p.id);
  });

  return (
    <Page title={L("Policies & Procedures", "السياسات والإجراءات")} code="UX-GOV-004" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      <div className="card-grid">
        <Card title={L("Policies", "السياسات")}>
          <Table rows={policies} empty={L("No policies.", "لا سياسات.")} onRowClick={(p) => open(p.id)} columns={[
            { label: L("Code", "الرمز"), render: (p) => p.code },
            { label: L("Title", "العنوان"), render: (p) => p.title },
            { label: "v", render: (p) => p.version },
            { label: L("Status", "الحالة"), render: (p) => <span className="badge">{p.status}</span> },
          ]} />
        </Card>
        <Card title={L("Reviews due (60 days)", "مراجعات مستحقة (60 يومًا)")}>
          <Table rows={upcoming} empty={L("No reviews due.", "لا مراجعات مستحقة.")} columns={[
            { label: L("Policy", "السياسة"), render: (p) => `${p.code} — ${p.title}` },
            { label: L("Review by", "موعد المراجعة"), render: (p) => fmtDate(p.nextReviewAt) },
          ]} />
        </Card>
        {detail && (
          <Card title={`${detail.policy.code} — ${detail.policy.title} (v${detail.policy.version})`} wide>
            <p style={{ marginTop: 0 }}>
              <span className="badge">{detail.policy.status}</span> · {detail.acknowledgements} {L("acknowledgements", "إقرار")} ·{" "}
              {L("next review", "المراجعة القادمة")} {fmtDate(detail.policy.nextReviewAt)}
            </p>
            <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", marginBottom: "1rem" }}>
              {(FLOW[detail.policy.status] ?? []).map((s) => (
                <button key={s} className="btn btn-secondary" onClick={() => post(`/api/policies/${detail.policy.id}/transition`, { status: s })}>→ {s}</button>))}
              {detail.policy.status === "Published" &&
                <button className="btn" onClick={() => post(`/api/policies/${detail.policy.id}/acknowledge`, {})}>{L("I acknowledge", "أقرّ بالاطلاع")}</button>}
              {detail.policy.status !== "Retired" &&
                <button className="btn btn-secondary" onClick={() => post(`/api/policies/${detail.policy.id}/retire`, {})}>{L("Retire", "إيقاف")}</button>}
            </div>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))", gap: "1rem" }}>
              <div>
                <Field label={L("Revise text (creates a new version)", "تعديل النص (ينشئ نسخة جديدة)")}>
                  <textarea className="input" rows={8} value={revision} onChange={(e) => setRevision(e.target.value)} /></Field>
                <Field label={L("Change note", "ملاحظة التغيير")}><input className="input" value={note} onChange={(e) => setNote(e.target.value)} /></Field>
                <button className="btn" style={{ marginTop: "0.5rem" }} disabled={!revision}
                  onClick={() => post(`/api/policies/${detail.policy.id}/versions`, { title: null, content: revision, changeNote: note || null, nextReviewAt: null })}>
                  {L("Save new version", "حفظ نسخة جديدة")}</button>
                <Field label={L("Schedule next review", "جدولة المراجعة القادمة")}>
                  <input className="input" type="date" value={review} onChange={(e) => setReview(e.target.value)} /></Field>
                <button className="btn btn-secondary" style={{ marginTop: "0.5rem" }} disabled={!review}
                  onClick={() => post(`/api/policies/${detail.policy.id}/review-schedule`, { nextReviewAt: new Date(review).toISOString(), ownerId: null })}>
                  {L("Schedule", "جدولة")}</button>
              </div>
              <div>
                <h4 style={{ marginTop: 0 }}>{L("Version history", "سجل النسخ")}</h4>
                <Table rows={detail.versions} empty={L("Only the original version.", "النسخة الأصلية فقط.")} columns={[
                  { label: "v", render: (v) => v.version },
                  { label: L("Note", "الملاحظة"), render: (v) => v.changeNote ?? "–" },
                  { label: L("Saved", "الحفظ"), render: (v) => fmtDate(v.createdAt) },
                ]} />
                <h4>{L("Procedures", "الإجراءات")}</h4>
                <Table rows={detail.procedures} empty={L("No procedures.", "لا إجراءات.")} columns={[
                  { label: L("Code", "الرمز"), render: (p) => p.code },
                  { label: L("Title", "العنوان"), render: (p) => p.title },
                ]} />
                <div style={{ display: "grid", gap: "0.5rem", marginTop: "0.5rem" }}>
                  <input className="input" placeholder={L("Procedure code", "رمز الإجراء")} value={pCode} onChange={(e) => setPCode(e.target.value)} />
                  <input className="input" placeholder={L("Procedure title", "عنوان الإجراء")} value={pTitle} onChange={(e) => setPTitle(e.target.value)} />
                  <textarea className="input" rows={3} placeholder={L("Steps", "الخطوات")} value={pSteps} onChange={(e) => setPSteps(e.target.value)} />
                  <button className="btn btn-secondary" disabled={!pCode || !pTitle || !pSteps}
                    onClick={() => post("/api/procedures", { policyId: detail.policy.id, code: pCode, title: pTitle, steps: pSteps })}>{L("Add procedure", "إضافة إجراء")}</button>
                </div>
              </div>
            </div>
          </Card>
        )}
        <Card title={L("New policy", "سياسة جديدة")}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <Field label={L("Code", "الرمز")}><input className="input" value={code} onChange={(e) => setCode(e.target.value)} /></Field>
            <Field label={L("Title", "العنوان")}><input className="input" value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
            <Field label={L("Text", "النص")}><textarea className="input" rows={5} value={content} onChange={(e) => setContent(e.target.value)} /></Field>
            <button className="btn" disabled={!code || !title} onClick={create}>{L("Create draft", "إنشاء مسودة")}</button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
