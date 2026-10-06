"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Kpi, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Counts = { approvals: number; tasks: number; overdue: number; unreadNotifications: number; activities: number };
type Me = { person: { id: string; fullName: string } | null; permissions: string[] };
type Approval = { id: string; entityType: string; entityId: string; status: string; dueAt: string; priority: string };
type Task = { id: string; title: string; status: string; dueAt: string; priority: string; progress: number };
type Notification = { id: string; title: string; body: string; channel: string; createdAt: string; readAt: string | null };

export default function MyWorkPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [me, setMe] = useState<Me | null>(null);
  const [counts, setCounts] = useState<Counts | null>(null);
  const [approvals, setApprovals] = useState<Approval[]>([]);
  const [submitted, setSubmitted] = useState<Approval[]>([]);
  const [tasks, setTasks] = useState<Task[]>([]);
  const [notes, setNotes] = useState<Notification[]>([]);

  const load = () => attempt(setErr, async () => {
    const who = await api<Me>("/api/auth/me");
    setMe(who);
    if (!who.person) throw new Error(L("Set 'Act as person ID' in the connection bar and connect again.", "أدخل معرّف الشخص في شريط الاتصال ثم اتصل مجددًا."));
    const pid = who.person.id;
    setCounts(await api<Counts>(`/api/inbox/counts?tenantId=${tenantId}`));
    setApprovals(await api<Approval[]>(`/api/approvals?tenantId=${tenantId}&assigneeId=${pid}&status=Pending`));
    setSubmitted(await api<Approval[]>(`/api/approvals/submitted?tenantId=${tenantId}`));
    setTasks((await api<Task[]>(`/api/tasks?tenantId=${tenantId}&assigneeId=${pid}`)).filter((t) => t.status !== "Done" && t.status !== "Verified"));
    setNotes(await api<Notification[]>(`/api/notifications?tenantId=${tenantId}&personId=${pid}`));
  });

  const decide = (id: string, approve: boolean) => attempt(setErr, async () => {
    await api(`/api/approvals/${id}/decide`, { method: "POST", body: JSON.stringify({ tenantId, approve, comment: null }) });
    await load();
  });
  const complete = (id: string) => attempt(setErr, async () => {
    await api(`/api/tasks/${id}/complete`, { method: "POST", body: JSON.stringify({ tenantId }) });
    await load();
  });
  const markRead = (id: string) => attempt(setErr, async () => {
    await api(`/api/notifications/${id}/read`, { method: "POST", body: JSON.stringify({ tenantId }) });
    await load();
  });

  return (
    <Page title={L("My Work", "عملي")} code="UX-INB-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Refresh", "تحديث")}</button>}>
      {me?.person && <p style={{ marginTop: 0 }}>{L("Signed in as", "مسجّل باسم")} <strong>{me.person.fullName}</strong></p>}
      <div className="card-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))" }}>
        <Kpi label={L("To approve", "للاعتماد")} value={counts?.approvals} />
        <Kpi label={L("Open tasks", "مهام مفتوحة")} value={counts?.tasks} />
        <Kpi label={L("Overdue", "متأخرة")} value={counts?.overdue} />
        <Kpi label={L("Unread", "غير مقروءة")} value={counts?.unreadNotifications} />
        <Kpi label={L("Activities", "أنشطة")} value={counts?.activities} />
      </div>
      <div className="card-grid" style={{ marginTop: "1.5rem" }}>
        <Card title={L("What do I need to approve?", "ما الذي أحتاج لاعتماده؟")}>
          <Table rows={approvals} empty={L("Nothing waiting.", "لا شيء بانتظارك.")} columns={[
            { label: L("Item", "البند"), render: (a) => a.entityType },
            { label: L("Due", "الاستحقاق"), render: (a) => fmtDate(a.dueAt) },
            { label: "", render: (a) => (<span style={{ display: "flex", gap: "0.25rem", flexWrap: "wrap" }}>
              <button className="btn" onClick={() => decide(a.id, true)}>{L("Approve", "اعتماد")}</button>
              <button className="btn btn-secondary" onClick={() => decide(a.id, false)}>{L("Reject", "رفض")}</button></span>) },
          ]} />
        </Card>
        <Card title={L("What do I need to do?", "ما الذي عليّ إنجازه؟")}>
          <Table rows={tasks} empty={L("No open tasks.", "لا مهام مفتوحة.")} columns={[
            { label: L("Task", "المهمة"), render: (t) => t.title },
            { label: L("Status", "الحالة"), render: (t) => <span className="badge">{t.status}</span> },
            { label: L("Due", "الاستحقاق"), render: (t) => fmtDate(t.dueAt) },
            { label: "", render: (t) => <button className="btn btn-secondary" onClick={() => complete(t.id)}>{L("Complete", "إنجاز")}</button> },
          ]} />
        </Card>
        <Card title={L("What am I waiting for?", "ما الذي أنتظره؟")}>
          <Table rows={submitted} empty={L("Nothing submitted.", "لم تقدّم شيئًا.")} columns={[
            { label: L("Item", "البند"), render: (a) => a.entityType },
            { label: L("Status", "الحالة"), render: (a) => <span className="badge">{a.status}</span> },
            { label: L("Due", "الاستحقاق"), render: (a) => fmtDate(a.dueAt) },
          ]} />
        </Card>
        <Card title={L("What do I need to know?", "ما الذي أحتاج لمعرفته؟")}>
          <Table rows={notes.filter((n) => n.channel === "InApp")} empty={L("No notifications.", "لا إشعارات.")} columns={[
            { label: L("Notification", "الإشعار"), render: (n) => <span style={{ fontWeight: n.readAt ? 400 : 700 }}>{n.title}</span> },
            { label: L("When", "الوقت"), render: (n) => fmtDate(n.createdAt) },
            { label: "", render: (n) => (n.readAt ? "✓" : <button className="btn btn-secondary" onClick={() => markRead(n.id)}>{L("Mark read", "مقروء")}</button>) },
          ]} />
        </Card>
      </div>
    </Page>
  );
}
