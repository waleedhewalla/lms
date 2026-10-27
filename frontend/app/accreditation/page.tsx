"use client";
import { useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { Card, Field, Kpi, Page, Table, attempt, fmtDay, useL, useTenant } from "../../components/Ui";

type Framework = { code: string; name: string; body: string; standards: number; criteria: number };
type Row = {
  standardId: string; standardCode: string; standardTitle: string; framework: string; criterionId: string; criterionCode: string;
  criterionText: string; evidence: number; evidenceByType: Record<string, number>; openMajor: number; openMinor: number; status: "Met" | "Partial" | "NotMet";
};
type Shared = { entityType: string; entityId: string; title: string | null; criteria: string[] };
type Crosswalk = { readiness: number; met: number; partial: number; notMet: number; rows: Row[]; shared: Shared[] };
type Action = { id: string; description: string; status: string; dueAt: string; assigneeId: string };
type Finding = { id: string; criterionId: string; severity: string; text: string; isClosed: boolean; actions: Action[] };
type EvidenceItem = { id: string; criterionId: string; entityType: string; entityId: string; note: string; title: string | null };

const ENTITY_TYPES = ["Document", "Policy", "Procedure", "Decision", "Meeting", "Committee", "Task", "Kpi", "Correspondence"];
const STATUS_COLOR: Record<string, string> = { Met: "#16a34a", Partial: "#ca8a04", NotMet: "#dc2626" };
const NEXT: Record<string, string> = { Open: "InProgress", InProgress: "Done", Done: "Verified" };

export default function AccreditationPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [ok, setOk] = useState("");
  const [frameworks, setFrameworks] = useState<Framework[]>([]);
  const [framework, setFramework] = useState("");
  const [cw, setCw] = useState<Crosswalk | null>(null);
  const [findings, setFindings] = useState<Finding[]>([]);
  const [selected, setSelected] = useState<Row | null>(null);
  const [evidence, setEvidence] = useState<EvidenceItem[]>([]);
  const [cite, setCite] = useState({ entityType: "Policy", entityId: "", note: "" });
  const [citeTo, setCiteTo] = useState<string[]>([]);
  const [finding, setFinding] = useState({ severity: "Minor", text: "" });
  const [action, setAction] = useState({ findingId: "", assigneeId: "", description: "", dueAt: "" });
  const [csv, setCsv] = useState({ framework: "", text: "standardCode,standardTitle,criterionCode,criterionText\n" });
  const [lang, setLang] = useState("en");

  const post = (path: string, body: object) => api(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
  const refresh = async (fw = framework) => {
    setCw(await api<Crosswalk>(`/api/quality/crosswalk?tenantId=${tenantId}${fw ? `&framework=${encodeURIComponent(fw)}` : ""}`));
    setFindings(await api<Finding[]>(`/api/quality/findings?tenantId=${tenantId}`));
  };
  const load = () => attempt(setErr, async () => {
    setOk("");
    setFrameworks(await api<Framework[]>("/api/quality/frameworks"));
    await refresh();
  });
  const choose = (r: Row) => attempt(setErr, async () => {
    setSelected(r);
    setEvidence(await api<EvidenceItem[]>(`/api/quality/evidence?tenantId=${tenantId}&criterionId=${r.criterionId}`));
  });
  const importFramework = (code: string) => attempt(setErr, async () => {
    const res = await post(`/api/quality/frameworks/${code}/import`, {}) as { standardsCreated: number; criteriaCreated: number };
    setOk(L(`Loaded ${code}: ${res.standardsCreated} standards and ${res.criteriaCreated} criteria added.`,
      `تم تحميل ${code}: أُضيف ${res.standardsCreated} معيار و${res.criteriaCreated} محك.`));
    setFramework(code); await refresh(code);
  });
  const importCsv = () => attempt(setErr, async () => {
    const res = await post("/api/quality/standards/import", { framework: csv.framework, csv: csv.text }) as { standardsCreated: number; criteriaCreated: number; updated: number };
    setOk(L(`Imported: ${res.standardsCreated} standards, ${res.criteriaCreated} criteria added, ${res.updated} updated.`,
      `تم الاستيراد: ${res.standardsCreated} معيار، ${res.criteriaCreated} محك جديد، ${res.updated} محدّث.`));
    await refresh();
  });
  const mapEvidence = () => attempt(setErr, async () => {
    const res = await post("/api/quality/evidence/map", { ...cite, entityId: cite.entityId.trim(), criterionIds: citeTo }) as { added: number; skipped: number; title: string };
    setOk(L(`"${res.title}" cited against ${res.added} criteria (${res.skipped} already linked).`,
      `تم ربط "${res.title}" بعدد ${res.added} من المحكات (${res.skipped} مرتبطة سابقًا).`));
    setCiteTo([]); await refresh();
    if (selected) await choose(selected);
  });
  const unlink = (id: string) => attempt(setErr, async () => {
    await api(`/api/quality/evidence/${id}?tenantId=${tenantId}`, { method: "DELETE" });
    await refresh(); if (selected) await choose(selected);
  });
  const addFinding = () => attempt(setErr, async () => {
    if (!selected) return;
    await post("/api/quality/findings", { criterionId: selected.criterionId, ...finding });
    setFinding({ severity: "Minor", text: "" }); await refresh();
  });
  const addAction = () => attempt(setErr, async () => {
    await post("/api/quality/corrective-actions", { findingId: action.findingId, assigneeId: action.assigneeId.trim(), description: action.description, dueAt: action.dueAt ? new Date(action.dueAt).toISOString() : null });
    setAction({ findingId: "", assigneeId: "", description: "", dueAt: "" }); await refresh();
  });
  const advance = (a: Action) => attempt(setErr, async () => { await post(`/api/quality/corrective-actions/${a.id}/status`, { status: NEXT[a.status] }); await refresh(); });
  const closeFinding = (f: Finding) => attempt(setErr, async () => { await post(`/api/quality/findings/${f.id}/close`, {}); await refresh(); });
  const selfStudy = () => attempt(setErr, async () => {
    const res = await fetch(`${apiBase()}/api/quality/self-study?tenantId=${tenantId}&framework=${encodeURIComponent(framework)}&lang=${lang}`, { headers: authHeaders() });
    if (!res.ok) throw new Error(`self-study → ${res.status}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a"); a.href = url; a.download = `self-study-${framework.toLowerCase()}.html`; a.click(); URL.revokeObjectURL(url);
  });
  const statusLabel: Record<string, string> = { Met: L("Met", "مستوفى"), Partial: L("Partial", "جزئي"), NotMet: L("Not met", "غير مستوفى") };
  const code = (id: string) => cw?.rows.find((r) => r.criterionId === id) ?? null;
  const loaded = new Set(cw?.rows.map((r) => r.framework) ?? []);

  return (
    <Page title={L("Accreditation", "الاعتماد الأكاديمي")} code="UX-QA-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      {ok && <p role="status" style={{ color: "var(--primary)" }}>{ok}</p>}
      <div className="card-grid">
        <Card title={L("Readiness", "الجاهزية")}>
          <Field label={L("Framework", "الإطار")}>
            <select className="input" value={framework} onChange={(e) => { setFramework(e.target.value); attempt(setErr, () => refresh(e.target.value)); }}>
              <option value="">{L("All frameworks", "كل الأطر")}</option>
              {[...new Set([...frameworks.map((f) => f.code), ...loaded])].map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </Field>
          {cw && (
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(90px, 1fr))", gap: "0.5rem", marginTop: "0.75rem" }}>
              <Kpi label={L("Readiness", "الجاهزية")} value={`${cw.readiness}%`} />
              <Kpi label={L("Met", "مستوفى")} value={cw.met} />
              <Kpi label={L("Partial", "جزئي")} value={cw.partial} />
              <Kpi label={L("Not met", "غير مستوفى")} value={cw.notMet} />
            </div>)}
          <div style={{ display: "flex", gap: "0.5rem", marginTop: "0.75rem", flexWrap: "wrap", alignItems: "end" }}>
            <Field label={L("Report language", "لغة التقرير")}>
              <select className="input" value={lang} onChange={(e) => setLang(e.target.value)}>
                <option value="en">English</option><option value="ar">العربية</option></select></Field>
            <button className="btn" disabled={!framework} onClick={selfStudy}>{L("Download self-study report", "تنزيل تقرير الدراسة الذاتية")}</button>
          </div>
          {!framework && <p style={{ fontSize: "0.8rem", opacity: 0.7 }}>{L("Choose a framework to generate its self-study report.", "اختر إطارًا لإنشاء تقرير الدراسة الذاتية.")}</p>}
        </Card>

        <Card title={L("Standards library", "مكتبة المعايير")}>
          <Table rows={frameworks} empty={L("Load to see the library.", "حمّل لعرض المكتبة.")} columns={[
            { label: L("Framework", "الإطار"), render: (f) => <span title={f.body}><strong>{f.code}</strong> {f.name}</span> },
            { label: L("Size", "الحجم"), render: (f) => `${f.standards} / ${f.criteria}` },
            { label: "", render: (f) => <button className="btn btn-secondary" onClick={() => importFramework(f.code)}>
              {loaded.has(f.code) ? L("Reload", "إعادة") : L("Load", "تحميل")}</button> },
          ]} />
          <p style={{ fontSize: "0.8rem", opacity: 0.7 }}>
            {L("Built-in criteria are short summaries. Paste the official criteria from your handbook below to replace them by code.",
               "المحكات المدمجة ملخصات موجزة. الصق المحكات الرسمية من دليل الهيئة أدناه لاستبدالها حسب الرمز.")}</p>
          <Field label={L("Framework code for CSV", "رمز الإطار للملف")}>
            <input className="input" value={csv.framework} onChange={(e) => setCsv({ ...csv, framework: e.target.value })} placeholder="NCAAA-I" /></Field>
          <Field label={L("Criteria CSV", "ملف المحكات CSV")}>
            <textarea className="input" rows={4} value={csv.text} onChange={(e) => setCsv({ ...csv, text: e.target.value })} style={{ fontFamily: "monospace" }} /></Field>
          <button className="btn btn-secondary" disabled={!csv.framework} onClick={importCsv}>{L("Import CSV", "استيراد")}</button>
        </Card>

        <Card title={L("Evidence crosswalk", "مصفوفة الشواهد")} wide>
          <Table rows={cw?.rows ?? []} onRowClick={choose} empty={L("No criteria loaded. Load a framework from the library.", "لا توجد محكات. حمّل إطارًا من المكتبة.")} columns={[
            { label: L("Standard", "المعيار"), render: (r) => <span title={r.standardTitle}>{r.standardCode}</span> },
            { label: L("Criterion", "المحك"), render: (r) => <span title={r.criterionText}><strong>{r.criterionCode}</strong> {r.criterionText.length > 70 ? `${r.criterionText.slice(0, 70)}…` : r.criterionText}</span> },
            { label: L("Evidence", "الشواهد"), render: (r) => <span title={Object.entries(r.evidenceByType).map(([k, v]) => `${k}: ${v}`).join(", ")}>{r.evidence}</span> },
            { label: L("Open findings", "ملاحظات مفتوحة"), render: (r) => (r.openMajor + r.openMinor > 0 ? `${r.openMajor} major / ${r.openMinor} minor` : "–") },
            { label: L("Status", "الحالة"), render: (r) => <span className="badge" style={{ background: STATUS_COLOR[r.status], color: "#fff" }}>{statusLabel[r.status]}</span> },
            { label: L("Cite", "ربط"), render: (r) => (
              <input type="checkbox" aria-label={`${L("Cite against", "ربط بـ")} ${r.standardCode} ${r.criterionCode}`} checked={citeTo.includes(r.criterionId)}
                onClick={(e) => e.stopPropagation()}
                onChange={(e) => setCiteTo(e.target.checked ? [...citeTo, r.criterionId] : citeTo.filter((x) => x !== r.criterionId))} />) },
          ]} />
        </Card>

        <Card title={L("Cite a record as evidence", "ربط سجل كشاهد")}>
          <p style={{ marginTop: 0, fontSize: "0.85rem", opacity: 0.8 }}>
            {L(`Tick the criteria in the crosswalk (${citeTo.length} selected), then name the record. One record can serve several frameworks.`,
               `حدّد المحكات في المصفوفة (${citeTo.length} محددة) ثم حدّد السجل. يمكن أن يخدم السجل الواحد عدة أطر.`)}</p>
          <Field label={L("Record type", "نوع السجل")}>
            <select className="input" value={cite.entityType} onChange={(e) => setCite({ ...cite, entityType: e.target.value })}>
              {ENTITY_TYPES.map((t) => <option key={t}>{t}</option>)}</select></Field>
          <Field label={L("Record ID", "معرّف السجل")}>
            <input className="input" value={cite.entityId} onChange={(e) => setCite({ ...cite, entityId: e.target.value })} /></Field>
          <Field label={L("Note (e.g. page or section)", "ملاحظة (مثل الصفحة أو القسم)")}>
            <input className="input" value={cite.note} onChange={(e) => setCite({ ...cite, note: e.target.value })} /></Field>
          <button className="btn" disabled={!cite.entityId || citeTo.length === 0} onClick={mapEvidence}>{L("Cite evidence", "ربط الشاهد")}</button>
        </Card>

        <Card title={selected ? `${selected.standardCode} ${selected.criterionCode}` : L("Criterion detail", "تفاصيل المحك")}>
          {!selected ? <p style={{ opacity: 0.7 }}>{L("Click a crosswalk row.", "اضغط على صف في المصفوفة.")}</p> : (<>
            <p style={{ marginTop: 0 }}>{selected.criterionText}</p>
            <Table rows={evidence} empty={L("No evidence yet.", "لا توجد شواهد بعد.")} columns={[
              { label: L("Record", "السجل"), render: (e) => `[${e.entityType}] ${e.title ?? L("(unavailable)", "(غير متاح)")}` },
              { label: L("Note", "ملاحظة"), render: (e) => e.note || "–" },
              { label: "", render: (e) => <button className="btn btn-secondary" onClick={() => unlink(e.id)}>{L("Unlink", "إلغاء الربط")}</button> },
            ]} />
            <div style={{ display: "grid", gap: "0.5rem", marginTop: "0.75rem" }}>
              <Field label={L("Finding severity", "درجة الملاحظة")}>
                <select className="input" value={finding.severity} onChange={(e) => setFinding({ ...finding, severity: e.target.value })}>
                  {["Observation", "Minor", "Major"].map((s) => <option key={s}>{s}</option>)}</select></Field>
              <Field label={L("Finding text", "نص الملاحظة")}>
                <input className="input" value={finding.text} onChange={(e) => setFinding({ ...finding, text: e.target.value })} /></Field>
              <button className="btn btn-secondary" disabled={!finding.text} onClick={addFinding}>{L("Record finding", "تسجيل ملاحظة")}</button>
            </div>
          </>)}
        </Card>

        <Card title={L("Findings and corrective actions", "الملاحظات والإجراءات التصحيحية")} wide>
          <Table rows={findings} empty={L("No findings.", "لا توجد ملاحظات.")} columns={[
            { label: L("Criterion", "المحك"), render: (f) => { const r = code(f.criterionId); return r ? `${r.standardCode} ${r.criterionCode}` : "–"; } },
            { label: L("Severity", "الدرجة"), render: (f) => <span className="badge">{f.severity}</span> },
            { label: L("Finding", "الملاحظة"), render: (f) => f.text },
            { label: L("Actions", "الإجراءات"), render: (f) => (
              <div style={{ display: "grid", gap: "0.25rem" }}>
                {f.actions.map((a) => (
                  <div key={a.id} style={{ display: "flex", gap: "0.5rem", alignItems: "center", flexWrap: "wrap" }}>
                    <span>{a.description} · {fmtDay(a.dueAt)} · <span className="badge">{a.status}</span></span>
                    {NEXT[a.status] && !f.isClosed && <button className="btn btn-secondary" onClick={() => advance(a)}>{L("Mark", "تحويل إلى")} {NEXT[a.status]}</button>}
                  </div>))}
              </div>) },
            { label: L("State", "الحالة"), render: (f) => f.isClosed ? L("Closed", "مغلقة")
              : <button className="btn btn-secondary" onClick={() => closeFinding(f)}>{L("Close", "إغلاق")}</button> },
          ]} />
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: "0.5rem", marginTop: "0.75rem", alignItems: "end" }}>
            <Field label={L("For finding", "للملاحظة")}>
              <select className="input" value={action.findingId} onChange={(e) => setAction({ ...action, findingId: e.target.value })}>
                <option value="">–</option>
                {findings.filter((f) => !f.isClosed).map((f) => <option key={f.id} value={f.id}>{f.text.slice(0, 40)}</option>)}</select></Field>
            <Field label={L("Assignee person ID", "معرّف المكلّف")}>
              <input className="input" value={action.assigneeId} onChange={(e) => setAction({ ...action, assigneeId: e.target.value })} /></Field>
            <Field label={L("Corrective action", "الإجراء التصحيحي")}>
              <input className="input" value={action.description} onChange={(e) => setAction({ ...action, description: e.target.value })} /></Field>
            <Field label={L("Due", "الاستحقاق")}>
              <input className="input" type="date" value={action.dueAt} onChange={(e) => setAction({ ...action, dueAt: e.target.value })} /></Field>
            <button className="btn btn-secondary" disabled={!action.findingId || !action.assigneeId || !action.description} onClick={addAction}>{L("Add action", "إضافة إجراء")}</button>
          </div>
          <p style={{ fontSize: "0.8rem", opacity: 0.7 }}>
            {L("A finding closes only when all its actions are verified, and the assignee cannot verify their own action.",
               "لا تُغلق الملاحظة إلا بعد التحقق من جميع إجراءاتها، ولا يحق للمكلّف التحقق من إجرائه.")}</p>
        </Card>

        <Card title={L("Evidence used across criteria", "شواهد مشتركة بين المحكات")}>
          <Table rows={cw?.shared ?? []} empty={L("No record is cited against more than one criterion yet.", "لا يوجد سجل مرتبط بأكثر من محك بعد.")} columns={[
            { label: L("Record", "السجل"), render: (s) => `[${s.entityType}] ${s.title ?? s.entityId.slice(0, 8)}` },
            { label: L("Criteria", "المحكات"), render: (s) => s.criteria.join(", ") },
          ]} />
        </Card>
      </div>
    </Page>
  );
}
