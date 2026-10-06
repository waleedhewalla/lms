"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Task = { id: string; title: string; assigneeId: string; status: string; dueAt: string; priority: string; progress: number; description: string };

export default function TasksPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [tasks, setTasks] = useState<Task[]>([]);
  const [assigneeFilter, setAssigneeFilter] = useState("");
  const [title, setTitle] = useState("");
  const [assigneeId, setAssigneeId] = useState("");
  const [dueAt, setDueAt] = useState("");
  const [priority, setPriority] = useState("Normal");
  const [description, setDescription] = useState("");

  const load = () => attempt(setErr, async () =>
    setTasks(await api<Task[]>(`/api/tasks?tenantId=${tenantId}${assigneeFilter ? `&assigneeId=${assigneeFilter}` : ""}`)));

  const create = () => attempt(setErr, async () => {
    await api("/api/tasks", { method: "POST", body: JSON.stringify({ tenantId, title, assigneeId, dueAt: new Date(dueAt).toISOString(), description, priority }) });
    setTitle(""); setDescription("");
    await load();
  });
  const act = (id: string, path: string, body: object = {}) => attempt(setErr, async () => {
    await api(`/api/tasks/${id}/${path}`, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
    await load();
  });
  const progress = (t: Task, value: number) => attempt(setErr, async () => {
    await api(`/api/tasks/${t.id}`, { method: "PATCH", body: JSON.stringify({ tenantId, progress: value }) });
    await load();
  });

  return (
    <Page title={L("Tasks & Actions", "المهام والإجراءات")} code="UX-TSK-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<>
        <input className="input" placeholder={L("Filter by assignee ID", "تصفية حسب المكلّف")} value={assigneeFilter}
          onChange={(e) => setAssigneeFilter(e.target.value)} style={{ width: "18rem" }} />
        <button className="btn" onClick={load}>{L("Load", "تحميل")}</button></>}>
      <div className="card-grid">
        <Card title={L("Tasks", "المهام")} wide>
          <Table rows={tasks} empty={L("No tasks.", "لا مهام.")} columns={[
            { label: L("Task", "المهمة"), render: (t) => <><strong>{t.title}</strong>{t.description && <div style={{ fontSize: "0.8rem", opacity: 0.7 }}>{t.description}</div>}</> },
            { label: L("Priority", "الأولوية"), render: (t) => t.priority },
            { label: L("Status", "الحالة"), render: (t) => <span className="badge">{t.status}</span> },
            { label: L("Progress", "الإنجاز"), render: (t) => (
              <select className="input" value={t.progress} onChange={(e) => progress(t, Number(e.target.value))} style={{ width: "6rem" }}
                disabled={t.status === "Done" || t.status === "Verified"}>
                {[0, 25, 50, 75, 100].map((p) => <option key={p} value={p}>{p}%</option>)}
              </select>) },
            { label: L("Due", "الاستحقاق"), render: (t) => fmtDate(t.dueAt) },
            { label: "", render: (t) => t.status === "Done"
                ? <button className="btn" onClick={() => act(t.id, "verify", { comment: null })}>{L("Verify", "تحقق")}</button>
                : t.status === "Verified" ? "✓"
                : <button className="btn btn-secondary" onClick={() => act(t.id, "complete")}>{L("Complete", "إنجاز")}</button> },
          ]} />
          <p style={{ fontSize: "0.8rem", opacity: 0.7, marginBottom: 0 }}>
            {L("Verification is done by someone other than the assignee (needs task:verify and a person in the connection bar).",
               "يتحقق من المهمة شخص غير المكلّف بها (يتطلب صلاحية task:verify ومعرّف شخص في شريط الاتصال).")}
          </p>
        </Card>
        <Card title={L("Assign a task", "إسناد مهمة")}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <Field label={L("Title", "العنوان")}><input className="input" value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
            <Field label={L("Assignee person ID", "معرّف المكلّف")}><input className="input" value={assigneeId} onChange={(e) => setAssigneeId(e.target.value)} /></Field>
            <Field label={L("Due", "الاستحقاق")}><input className="input" type="datetime-local" value={dueAt} onChange={(e) => setDueAt(e.target.value)} /></Field>
            <Field label={L("Priority", "الأولوية")}>
              <select className="input" value={priority} onChange={(e) => setPriority(e.target.value)}>
                {["Low", "Normal", "High", "Urgent"].map((p) => <option key={p}>{p}</option>)}</select>
            </Field>
            <Field label={L("Description", "الوصف")}><textarea className="input" rows={3} value={description} onChange={(e) => setDescription(e.target.value)} /></Field>
            <button className="btn" onClick={create} disabled={!title || !assigneeId || !dueAt}>{L("Create task", "إنشاء المهمة")}</button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
