"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Req = { id: string; number: string; category: string; title: string; status: string; createdAt: string; submitterId: string; formId: string | null };
type FormDef = { id: string; code: string; name: string; category: string; schemaJson: string };
type FieldDef = { key: string; label?: string; type?: string; required?: boolean; options?: string[] };
type Detail = { request: Req; submissions: { dataJson: string }[]; approvals: { id: string; status: string; dueAt: string }[]; workflow: { status: string } | null };

const parseFields = (json: string): FieldDef[] => {
  try { const v = JSON.parse(json); return Array.isArray(v) ? v : []; } catch { return []; }
};

export default function RequestsPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [requests, setRequests] = useState<Req[]>([]);
  const [categories, setCategories] = useState<string[]>([]);
  const [forms, setForms] = useState<FormDef[]>([]);
  const [detail, setDetail] = useState<Detail | null>(null);
  const [status, setStatus] = useState("");
  // new request
  const [category, setCategory] = useState("IT");
  const [title, setTitle] = useState("");
  const [formId, setFormId] = useState("");
  const [values, setValues] = useState<Record<string, string>>({});
  const [submitterId, setSubmitterId] = useState("");
  // submit
  const [reviewerId, setReviewerId] = useState("");
  const [workflowCode, setWorkflowCode] = useState("");
  // form builder
  const [fCode, setFCode] = useState("");
  const [fName, setFName] = useState("");
  const [fCategory, setFCategory] = useState("HR");
  const [fSchema, setFSchema] = useState('[{"key":"reason","label":"Reason","type":"text","required":true}]');

  const form = forms.find((f) => f.id === formId);
  const fields = form ? parseFields(form.schemaJson) : [];

  const load = () => attempt(setErr, async () => {
    setCategories(await api<string[]>("/api/requests/categories"));
    setForms(await api<FormDef[]>(`/api/forms?tenantId=${tenantId}`));
    setRequests(await api<Req[]>(`/api/requests?tenantId=${tenantId}${status ? `&status=${status}` : ""}`));
  });
  const open = (id: string) => attempt(setErr, async () => setDetail(await api<Detail>(`/api/requests/${id}?tenantId=${tenantId}`)));

  const create = () => attempt(setErr, async () => {
    const dataJson = form ? JSON.stringify(values) : null;
    if (form) {
      const check = await api<{ valid: boolean; errors: string[] }>(`/api/forms/${form.id}/validate`, { method: "POST", body: JSON.stringify({ tenantId, dataJson }) });
      if (!check.valid) throw new Error(L("Missing required fields: ", "حقول مطلوبة ناقصة: ") + check.errors.join(", "));
    }
    const r = await api<Req>("/api/requests", { method: "POST", body: JSON.stringify({ tenantId, category, title, submitterId, formId: form?.id ?? null, dataJson }) });
    setTitle(""); setValues({});
    await load(); await open(r.id);
  });
  const submit = () => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/requests/${detail.request.id}/submit`, { method: "POST",
      body: JSON.stringify({ tenantId, reviewerId: reviewerId || null, workflowCode: workflowCode || null }) });
    await load(); await open(detail.request.id);
  });
  const cancel = () => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/requests/${detail.request.id}/cancel`, { method: "POST", body: JSON.stringify({ tenantId, reason: null }) });
    await load(); await open(detail.request.id);
  });
  const createForm = () => attempt(setErr, async () => {
    JSON.parse(fSchema);
    await api("/api/forms", { method: "POST", body: JSON.stringify({ tenantId, code: fCode, name: fName, category: fCategory, schemaJson: fSchema }) });
    setFCode(""); setFName("");
    await load();
  });

  return (
    <Page title={L("Request Center", "مركز الطلبات")} code="UX-REQ-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<>
        <select className="input" value={status} onChange={(e) => setStatus(e.target.value)} style={{ width: "auto" }}>
          <option value="">{L("All statuses", "كل الحالات")}</option>
          {["Draft", "Submitted", "InReview", "ChangesRequested", "Approved", "Rejected", "Closed"].map((s) => <option key={s}>{s}</option>)}
        </select>
        <button className="btn" onClick={load}>{L("Load", "تحميل")}</button></>}>
      <div className="card-grid">
        <Card title={L("Requests", "الطلبات")} wide>
          <Table rows={requests} empty={L("No requests.", "لا طلبات.")} onRowClick={(r) => open(r.id)} columns={[
            { label: L("Number", "الرقم"), render: (r) => r.number },
            { label: L("Title", "العنوان"), render: (r) => r.title },
            { label: L("Category", "الفئة"), render: (r) => r.category },
            { label: L("Status", "الحالة"), render: (r) => <span className="badge">{r.status}</span> },
            { label: L("Created", "الإنشاء"), render: (r) => fmtDate(r.createdAt) },
          ]} />
        </Card>
        <Card title={L("New request", "طلب جديد")}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <Field label={L("Category", "الفئة")}>
              <select className="input" value={category} onChange={(e) => setCategory(e.target.value)}>{categories.map((c) => <option key={c}>{c}</option>)}</select>
            </Field>
            <Field label={L("Title", "العنوان")}><input className="input" value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
            <Field label={L("Submitter person ID", "معرّف مقدّم الطلب")}><input className="input" value={submitterId} onChange={(e) => setSubmitterId(e.target.value)} /></Field>
            <Field label={L("Form", "النموذج")}>
              <select className="input" value={formId} onChange={(e) => { setFormId(e.target.value); setValues({}); }}>
                <option value="">{L("No form", "بدون نموذج")}</option>
                {forms.map((f) => <option key={f.id} value={f.id}>{f.code} — {f.name}</option>)}
              </select>
            </Field>
            {fields.map((f) => (
              <Field key={f.key} label={`${f.label ?? f.key}${f.required ? " *" : ""}`}>
                {f.options?.length
                  ? <select className="input" value={values[f.key] ?? ""} onChange={(e) => setValues({ ...values, [f.key]: e.target.value })}>
                      <option value="" />{f.options.map((o) => <option key={o}>{o}</option>)}</select>
                  : <input className="input" type={f.type === "number" ? "number" : f.type === "date" ? "date" : "text"}
                      value={values[f.key] ?? ""} onChange={(e) => setValues({ ...values, [f.key]: e.target.value })} />}
              </Field>
            ))}
            <button className="btn" onClick={create} disabled={!title || !submitterId}>{L("Create draft", "إنشاء مسودة")}</button>
          </div>
        </Card>
        <Card title={detail ? `${detail.request.number} — ${detail.request.title}` : L("Request detail", "تفاصيل الطلب")}>
          {!detail ? <p style={{ opacity: 0.7 }}>{L("Select a request.", "اختر طلبًا.")}</p> : (
            <div style={{ display: "grid", gap: "0.75rem" }}>
              <div>{L("Status", "الحالة")}: <span className="badge">{detail.request.status}</span>{detail.workflow && <> · {L("Workflow", "سير العمل")}: {detail.workflow.status}</>}</div>
              {detail.submissions[0] && <pre style={{ whiteSpace: "pre-wrap", fontSize: "0.85rem", margin: 0 }}>{detail.submissions[0].dataJson}</pre>}
              <Table rows={detail.approvals} empty={L("No approvals yet.", "لا اعتمادات بعد.")} columns={[
                { label: L("Approval", "الاعتماد"), render: (a) => <span className="badge">{a.status}</span> },
                { label: L("Due", "الاستحقاق"), render: (a) => fmtDate(a.dueAt) },
              ]} />
              {detail.request.status === "Draft" || detail.request.status === "ChangesRequested" ? (<>
                <Field label={L("Reviewer person ID", "معرّف المراجع")}><input className="input" value={reviewerId} onChange={(e) => setReviewerId(e.target.value)} /></Field>
                <Field label={L("…or workflow code", "…أو رمز سير العمل")}><input className="input" value={workflowCode} onChange={(e) => setWorkflowCode(e.target.value)} /></Field>
                <div style={{ display: "flex", gap: "0.5rem" }}>
                  <button className="btn" onClick={submit} disabled={!reviewerId && !workflowCode}>{L("Submit", "إرسال")}</button>
                  <button className="btn btn-secondary" onClick={cancel}>{L("Cancel request", "إلغاء الطلب")}</button>
                </div></>) : null}
            </div>)}
        </Card>
        <Card title={L("Form builder", "منشئ النماذج")}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <Field label={L("Code", "الرمز")}><input className="input" value={fCode} onChange={(e) => setFCode(e.target.value)} /></Field>
            <Field label={L("Name", "الاسم")}><input className="input" value={fName} onChange={(e) => setFName(e.target.value)} /></Field>
            <Field label={L("Category", "الفئة")}>
              <select className="input" value={fCategory} onChange={(e) => setFCategory(e.target.value)}>{categories.map((c) => <option key={c}>{c}</option>)}</select>
            </Field>
            <Field label={L("Fields (JSON)", "الحقول (JSON)")}><textarea className="input" rows={5} value={fSchema} onChange={(e) => setFSchema(e.target.value)} /></Field>
            <button className="btn btn-secondary" onClick={createForm} disabled={!fCode || !fName}>{L("Save form", "حفظ النموذج")}</button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
