"use client";
import { useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, fmtDay, useL, useTenant } from "../../components/Ui";

type Schedule = { id: string; code: string; name: string; entityType: string; recordClass: string; retentionYears: number | null; disposition: string; legalReference: string | null };
type Candidate = { entityType: string; entityId: string; reference: string; title: string; recordDate: string; eligibleAt: string };
type Batch = { id: string; scheduleId: string; disposition: string; status: string; proposedBy: string; proposedAt: string; decidedBy: string | null; executedAt: string | null };
type Item = { id: string; reference: string; title: string; recordDate: string; outcome: string; note: string | null };
type Hold = { id: string; entityType: string; entityId: string; reason: string; placedBy: string; placedAt: string };

const CLASSES = ["Administrative", "Financial", "Specialized"];
const DISPOSITIONS = ["Destroy", "TransferToArchive", "Permanent"];

export default function RecordsPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [schedules, setSchedules] = useState<Schedule[]>([]);
  const [batches, setBatches] = useState<{ batch: Batch; items: number }[]>([]);
  const [holds, setHolds] = useState<Hold[]>([]);
  const [preview, setPreview] = useState<{ schedule: Schedule; items: Candidate[] } | null>(null);
  const [detail, setDetail] = useState<{ batch: Batch; items: Item[] } | null>(null);
  const [form, setForm] = useState({ code: "", name: "", entityType: "Correspondence", recordClass: "Administrative", retentionYears: "10", disposition: "Destroy", legalReference: "" });
  const [hold, setHold] = useState({ entityType: "Correspondence", entityId: "", reason: "" });
  const [note, setNote] = useState("");

  const load = () => attempt(setErr, async () => {
    setSchedules(await api<Schedule[]>(`/api/records/schedules?tenantId=${tenantId}`));
    setBatches(await api<{ batch: Batch; items: number }[]>(`/api/records/disposals?tenantId=${tenantId}`));
    setHolds(await api<Hold[]>(`/api/records/holds?tenantId=${tenantId}`));
  });
  const post = (path: string, body: object) => api(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
  const openBatch = (id: string) => attempt(setErr, async () => setDetail(await api(`/api/records/disposals/${id}?tenantId=${tenantId}`)));
  const act = (id: string, action: "approve" | "reject" | "execute") => attempt(setErr, async () => {
    await post(`/api/records/disposals/${id}/${action}`, { note: note || null });
    setNote(""); await load(); await openBatch(id);
  });
  const manifest = (b: Batch) => attempt(setErr, async () => {
    const res = await fetch(`${apiBase()}/api/records/disposals/${b.id}/manifest.csv?tenantId=${tenantId}`, { headers: authHeaders() });
    if (!res.ok) throw new Error(`manifest → ${res.status}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a"); a.href = url; a.download = `disposal-${b.id.slice(0, 8)}.csv`; a.click(); URL.revokeObjectURL(url);
  });
  const scheduleName = (id: string) => schedules.find((s) => s.id === id)?.code ?? id.slice(0, 8);
  const retention = (s: Schedule) => (s.disposition === "Permanent" ? L("Permanent", "دائم") : `${s.retentionYears} ${L("years", "سنوات")}`);

  return (
    <Page title={L("Records Retention", "حفظ السجلات والإتلاف")} code="UX-REC-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      <div className="card-grid">
        <Card title={L("Retention schedules", "جداول مدد الحفظ")} wide>
          <Table rows={schedules} empty={L("No schedules yet.", "لا توجد جداول بعد.")} columns={[
            { label: L("Code", "الرمز"), render: (s) => s.code },
            { label: L("Records", "السجلات"), render: (s) => `${s.name} (${s.entityType})` },
            { label: L("Class", "الفئة"), render: (s) => s.recordClass },
            { label: L("Keep", "مدة الحفظ"), render: (s) => retention(s) },
            { label: L("Then", "ثم"), render: (s) => <span className="badge">{s.disposition}</span> },
            { label: "", render: (s) => s.disposition === "Permanent" ? "–" : (
              <button className="btn btn-secondary" onClick={() => attempt(setErr, async () =>
                setPreview({ schedule: s, items: await api<Candidate[]>(`/api/records/schedules/${s.id}/eligible?tenantId=${tenantId}`) }))}>
                {L("Eligible now", "المستحق الآن")}</button>) },
          ]} />
        </Card>
        {preview && (
          <Card title={`${L("Eligible under", "المستحق ضمن")} ${preview.schedule.code} (${preview.items.length})`} wide>
            <Table rows={preview.items} empty={L("Nothing is past retention.", "لا شيء تجاوز مدة الحفظ.")} columns={[
              { label: L("Reference", "المرجع"), render: (c) => c.reference },
              { label: L("Title", "العنوان"), render: (c) => c.title },
              { label: L("Dated", "التاريخ"), render: (c) => fmtDay(c.recordDate) },
              { label: L("Eligible since", "مستحق منذ"), render: (c) => fmtDay(c.eligibleAt) },
            ]} />
            <button className="btn" style={{ marginTop: "0.75rem" }} disabled={preview.items.length === 0}
              onClick={() => attempt(setErr, async () => { await post("/api/records/disposals", { scheduleId: preview.schedule.id }); setPreview(null); await load(); })}>
              {L("Propose disposal batch", "اقتراح دفعة إتلاف")}</button>
          </Card>
        )}
        <Card title={L("Disposal batches", "دفعات الإتلاف")} wide>
          <Table rows={batches} empty={L("No batches.", "لا توجد دفعات.")} onRowClick={(b) => openBatch(b.batch.id)} columns={[
            { label: L("Schedule", "الجدول"), render: (b) => scheduleName(b.batch.scheduleId) },
            { label: L("Action", "الإجراء"), render: (b) => b.batch.disposition },
            { label: L("Records", "السجلات"), render: (b) => b.items },
            { label: L("Status", "الحالة"), render: (b) => <span className="badge">{b.batch.status}</span> },
            { label: L("Proposed", "اقتُرحت"), render: (b) => `${fmtDay(b.batch.proposedAt)} · ${b.batch.proposedBy}` },
          ]} />
        </Card>
        {detail && (
          <Card title={`${scheduleName(detail.batch.scheduleId)} — ${detail.batch.status}`} wide>
            <p style={{ marginTop: 0, fontSize: "0.85rem", opacity: 0.8 }}>
              {L("The proposer cannot approve their own batch. Destroyed correspondence keeps its register entry; document files are deleted and the deletion confirmed.",
                 "لا يمكن لمقترح الدفعة اعتمادها. تحتفظ المراسلات المتلفة بقيدها في السجل، وتُحذف ملفات الوثائق مع التحقق من الحذف.")}</p>
            <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", alignItems: "flex-end", marginBottom: "0.75rem" }}>
              <Field label={L("Note", "ملاحظة")}><input className="input" value={note} onChange={(e) => setNote(e.target.value)} /></Field>
              {detail.batch.status === "Proposed" && <>
                <button className="btn" onClick={() => act(detail.batch.id, "approve")}>{L("Approve", "اعتماد")}</button>
                <button className="btn btn-secondary" disabled={!note} onClick={() => act(detail.batch.id, "reject")}>{L("Reject", "رفض")}</button></>}
              {(detail.batch.status === "Approved" || detail.batch.status === "ExecutedWithErrors") &&
                <button className="btn" onClick={() => act(detail.batch.id, "execute")}>
                  {detail.batch.status === "Approved" ? L("Execute", "تنفيذ") : L("Retry pending", "إعادة المعلّق")}</button>}
              <button className="btn btn-secondary" onClick={() => manifest(detail.batch)}>⬇ {L("Manifest (CSV)", "المحضر (CSV)")}</button>
            </div>
            <Table rows={detail.items} empty="–" columns={[
              { label: L("Reference", "المرجع"), render: (i) => i.reference },
              { label: L("Title", "العنوان"), render: (i) => i.title },
              { label: L("Dated", "التاريخ"), render: (i) => fmtDay(i.recordDate) },
              { label: L("Outcome", "النتيجة"), render: (i) => <span className="badge">{i.outcome}</span> },
              { label: L("Note", "ملاحظة"), render: (i) => i.note ?? "–" },
            ]} />
          </Card>
        )}
        <Card title={L("New schedule", "جدول جديد")}>
          <div style={{ display: "grid", gap: "0.6rem" }}>
            <Field label={L("Code", "الرمز")}><input className="input" value={form.code} onChange={(e) => setForm({ ...form, code: e.target.value })} /></Field>
            <Field label={L("Name", "الاسم")}><input className="input" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></Field>
            <Field label={L("Applies to", "ينطبق على")}>
              <select className="input" value={form.entityType} onChange={(e) => setForm({ ...form, entityType: e.target.value })}>
                <option value="Correspondence">{L("Correspondence", "المراسلات")}</option><option value="Document">{L("Documents", "الوثائق")}</option></select></Field>
            <Field label={L("Record class", "فئة السجل")}>
              <select className="input" value={form.recordClass} onChange={(e) => setForm({ ...form, recordClass: e.target.value })}>
                {CLASSES.map((c) => <option key={c}>{c}</option>)}</select></Field>
            <Field label={L("After retention", "بعد مدة الحفظ")}>
              <select className="input" value={form.disposition} onChange={(e) => setForm({ ...form, disposition: e.target.value })}>
                {DISPOSITIONS.map((d) => <option key={d}>{d}</option>)}</select></Field>
            {form.disposition !== "Permanent" &&
              <Field label={L("Keep for (years)", "مدة الحفظ (سنوات)")}><input className="input" type="number" min={1} max={100} value={form.retentionYears}
                onChange={(e) => setForm({ ...form, retentionYears: e.target.value })} /></Field>}
            <Field label={L("Legal reference", "المرجع النظامي")}><input className="input" value={form.legalReference} onChange={(e) => setForm({ ...form, legalReference: e.target.value })} /></Field>
            <button className="btn" disabled={!form.code || !form.name} onClick={() => attempt(setErr, async () => {
              await post("/api/records/schedules", { ...form, retentionYears: form.disposition === "Permanent" ? null : Number(form.retentionYears), legalReference: form.legalReference || null });
              setForm({ ...form, code: "", name: "", legalReference: "" }); await load();
            })}>{L("Create schedule", "إنشاء الجدول")}</button>
          </div>
        </Card>
        <Card title={L("Legal holds", "أوامر التحفظ")}>
          <Table rows={holds} empty={L("No active holds.", "لا توجد أوامر تحفظ نشطة.")} columns={[
            { label: L("Record", "السجل"), render: (h) => `${h.entityType} ${h.entityId.slice(0, 8)}` },
            { label: L("Reason", "السبب"), render: (h) => h.reason },
            { label: L("Placed", "تاريخ الوضع"), render: (h) => fmtDate(h.placedAt) },
            { label: "", render: (h) => <button className="btn btn-secondary" onClick={() => attempt(setErr, async () => {
                await post(`/api/records/holds/${h.id}/release`, { note: null }); await load(); })}>{L("Release", "رفع")}</button> },
          ]} />
          <div style={{ display: "grid", gap: "0.5rem", marginTop: "0.75rem" }}>
            <select className="input" value={hold.entityType} onChange={(e) => setHold({ ...hold, entityType: e.target.value })}>
              <option value="Correspondence">{L("Correspondence", "المراسلات")}</option><option value="Document">{L("Document", "وثيقة")}</option></select>
            <input className="input" placeholder={L("Record ID", "معرّف السجل")} value={hold.entityId} onChange={(e) => setHold({ ...hold, entityId: e.target.value })} />
            <input className="input" placeholder={L("Reason (e.g. litigation, audit)", "السبب (مثل: قضية، تدقيق)")} value={hold.reason} onChange={(e) => setHold({ ...hold, reason: e.target.value })} />
            <button className="btn btn-secondary" disabled={!hold.entityId || !hold.reason} onClick={() => attempt(setErr, async () => {
              await post("/api/records/holds", hold); setHold({ ...hold, entityId: "", reason: "" }); await load(); })}>{L("Place hold", "وضع تحفظ")}</button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
