"use client";
import { useCallback, useEffect, useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { config } from "../../lib/config";
import { personIdOf } from "../../lib/modules";
import { claims } from "../../lib/oidc";
import { discardQueue, enqueue, listQueue, replay, type DropReason, type QueueItem, type ReplayResult } from "../../lib/offlineQueue";
import { browserStorage, loadSnapshot, purgeAll, purgeIfOtherPerson, saveSnapshot } from "../../lib/offlineStore";
import { Card, Kpi, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Counts = { approvals: number; tasks: number; overdue: number; unreadNotifications: number; activities: number };
type Me = { person: { id: string; fullName: string } | null; permissions: string[] };
type Approval = { id: string; entityType: string; entityId?: string; status: string; dueAt: string; priority: string };
type Task = { id: string; title: string; status: string; dueAt: string; priority: string; progress: number };
type Notification = { id: string; title: string; body: string; channel: string; createdAt: string; readAt: string | null };

const dropText = (reason: DropReason, L: (en: string, ar: string) => string) => ({
  "already-done": L("already decided or completed by someone else; nothing was changed", "سبق البتّ فيه أو إنجازه؛ لم يتغير شيء"),
  "not-allowed": L("you can no longer do this", "لم يعد مسموحًا لك بذلك"),
  "not-found": L("no longer exists", "لم يعد موجودًا"),
  "other-person": L("belonged to another person and was removed", "يخص شخصًا آخر وتمت إزالته"),
  "rejected": L("the server did not accept it", "لم يقبله الخادم"),
}[reason]);

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

  // Offline copy (only when the deployment turns it on): what is on screen came from the device, and actions wait in a queue.
  const [enabled, setEnabled] = useState(false);
  const [offline, setOffline] = useState(false);
  const [savedAt, setSavedAt] = useState<string | null>(null);
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [report, setReport] = useState<ReplayResult | null>(null);
  useEffect(() => setEnabled(config().offlineData), []);

  const isNetworkError = (e: unknown) => e instanceof TypeError || (typeof navigator !== "undefined" && navigator.onLine === false);
  const storedPersonId = () => personIdOf(claims({ ignoreExpiry: true }));
  const refreshQueue = async () => { if (config().offlineData) setQueue(await listQueue(browserStorage())); };

  const load = useCallback(() => attempt(setErr, async () => {
    const storage = browserStorage();
    try {
      const who = await api<Me>("/api/auth/me");
      setMe(who);
      if (!who.person) throw new Error(L("Set 'Act as person ID' in the connection bar and connect again.", "أدخل معرّف الشخص في شريط الاتصال ثم اتصل مجددًا."));
      const pid = who.person.id;
      const [c, a, sub, t, n] = await Promise.all([
        api<Counts>(`/api/inbox/counts?tenantId=${tenantId}`),
        api<Approval[]>(`/api/approvals?tenantId=${tenantId}&assigneeId=${pid}&status=Pending`),
        api<Approval[]>(`/api/approvals/submitted?tenantId=${tenantId}`),
        api<Task[]>(`/api/tasks?tenantId=${tenantId}&assigneeId=${pid}`),
        api<Notification[]>(`/api/notifications?tenantId=${tenantId}&personId=${pid}`),
      ]);
      const open = t.filter((x) => x.status !== "Done" && x.status !== "Verified");
      setCounts(c); setApprovals(a); setSubmitted(sub); setTasks(open); setNotes(n);
      setOffline(false);
      if (config().offlineData) {
        await purgeIfOtherPerson(storage, pid);
        const now = new Date().toISOString();
        await saveSnapshot(storage, { tenantId, personId: pid, personName: who.person.fullName, savedAt: now, counts: c,
          approvals: a, tasks: open });
        setSavedAt(now);
      }
    } catch (e) {
      // No network: show the copy kept on this device, if the deployment keeps one and it is this person's.
      if (!config().offlineData || !isNetworkError(e)) throw e;
      const pid = storedPersonId();
      const snap = pid ? await loadSnapshot(storage, new Date(), pid) : undefined;
      if (!snap) throw new Error(L("You are offline and there is no saved copy of your work on this device.", "أنت غير متصل ولا توجد نسخة محفوظة من عملك على هذا الجهاز."));
      setMe({ person: { id: snap.personId, fullName: snap.personName }, permissions: [] });
      setCounts(snap.counts ? { ...snap.counts, unreadNotifications: 0, activities: 0 } : null);
      setApprovals(snap.approvals.map((x) => ({ ...x, status: "Pending" })));
      setTasks(snap.tasks); setSubmitted([]); setNotes([]);
      setSavedAt(snap.savedAt); setOffline(true);
    }
    await refreshQueue();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [tenantId]);

  // Coming back online refreshes the page; queued actions are never sent without the person's say-so.
  useEffect(() => {
    const back = () => { void load(); };
    window.addEventListener("online", back);
    return () => window.removeEventListener("online", back);
  }, [load]);
  useEffect(() => { if (tenantId) void load(); }, [tenantId, load]);

  const personIdNow = me?.person?.id ?? storedPersonId() ?? "";
  const queuedIds = new Set(queue.map((q) => q.targetId));

  /** Online: act now. Offline (or the connection drops mid-action): keep it for the person to send later. */
  const act = (kind: QueueItem["kind"], targetId: string, label: string, path: string, body: Record<string, unknown>, ) => attempt(setErr, async () => {
    const keep = async () => { await enqueue(browserStorage(), { kind, targetId, label, tenantId, personId: personIdNow }, new Date()); await refreshQueue(); };
    if (config().offlineData && (offline || navigator.onLine === false)) { await keep(); return; }
    try {
      await api(path, { method: "POST", body: JSON.stringify(body) });
    } catch (e) {
      if (config().offlineData && e instanceof TypeError) { await keep(); return; }
      throw e;
    }
    await load();
  });
  const decide = (id: string, approve: boolean) =>
    act(approve ? "approve" : "reject", id, `${approve ? L("Approve", "اعتماد") : L("Reject", "رفض")} · ${approvals.find((a) => a.id === id)?.entityType ?? ""}`,
      `/api/approvals/${id}/decide`, { tenantId, approve, comment: null });
  const complete = (id: string) =>
    act("complete", id, `${L("Complete", "إنجاز")} · ${tasks.find((t) => t.id === id)?.title ?? ""}`, `/api/tasks/${id}/complete`, { tenantId });

  const send = async (item: QueueItem): Promise<number> => {
    const path = item.kind === "complete" ? `/api/tasks/${item.targetId}/complete` : `/api/approvals/${item.targetId}/decide`;
    const body = item.kind === "complete" ? { tenantId: item.tenantId } : { tenantId: item.tenantId, approve: item.kind === "approve", comment: null };
    try { return (await fetch(`${apiBase()}${path}`, { method: "POST", headers: authHeaders(), body: JSON.stringify(body) })).status; }
    catch { return 0; }
  };
  const sendQueue = () => attempt(setErr, async () => {
    setReport(await replay(browserStorage(), send, personIdNow));
    await load();
  });
  const discard = () => attempt(setErr, async () => { await discardQueue(browserStorage()); setReport(null); await refreshQueue(); });
  const clearCopy = () => attempt(setErr, async () => { await purgeAll(browserStorage()); setQueue([]); setReport(null); setSavedAt(null); });

  const markRead = (id: string) => attempt(setErr, async () => {
    await api(`/api/notifications/${id}/read`, { method: "POST", body: JSON.stringify({ tenantId }) });
    await load();
  });

  return (
    <Page title={L("My Work", "عملي")} code="UX-INB-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<>
        <button className="btn" onClick={load}>{L("Refresh", "تحديث")}</button>
        {enabled && savedAt && <button type="button" className="btn btn-secondary" onClick={clearCopy}>{L("Clear offline copy", "مسح النسخة المحفوظة")}</button>}</>}>
      {me?.person && <p style={{ marginTop: 0 }}>{L("Signed in as", "مسجّل باسم")} <strong>{me.person.fullName}</strong></p>}
      {enabled && offline && (
        <div role="status" className="glass-card" style={{ marginBottom: "1rem" }}>
          <strong>{L("You are offline.", "أنت غير متصل.")}</strong> {L("Showing the copy saved on this device at", "تُعرض النسخة المحفوظة على هذا الجهاز في")} {fmtDate(savedAt)}.
          {" "}{L("Approvals and tasks you act on now wait below until you review and send them.", "الاعتمادات والمهام التي تتصرف بها الآن تنتظر أدناه حتى تراجعها وترسلها.")}
        </div>)}
      {enabled && queue.length > 0 && (
        <div className="glass-card" style={{ marginBottom: "1rem" }}>
          <strong>{L("Waiting to be sent", "بانتظار الإرسال")} ({queue.length})</strong>
          <ul style={{ margin: "0.5rem 0", paddingInlineStart: "1.25rem" }}>
            {queue.map((q) => <li key={q.id}>{q.label} <span style={{ opacity: 0.7 }}>({fmtDate(q.createdAt)})</span></li>)}
          </ul>
          <p style={{ margin: "0 0 0.5rem", fontSize: "0.85rem", opacity: 0.8 }}>{L("These were decided on this device while offline. The server checks each one again when it is sent; one that is no longer allowed is dropped and reported.", "تم اتخاذ هذه القرارات على هذا الجهاز دون اتصال. يتحقق الخادم من كل منها مجددًا عند الإرسال؛ وما لم يعد مسموحًا يُسقط ويُبلَّغ عنه.")}</p>
          <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}>
            <button type="button" className="btn" onClick={sendQueue} disabled={offline}>{L("Send now", "إرسال الآن")}</button>
            <button type="button" className="btn btn-secondary" onClick={discard}>{L("Discard these", "تجاهلها")}</button>
          </div>
        </div>)}
      {report && (
        <div role="status" className="glass-card" style={{ marginBottom: "1rem" }}>
          <strong>{L("Sent:", "أُرسل:")} {report.sent.length}</strong>
          {report.dropped.map((d) => <p key={d.item.id} style={{ margin: "0.25rem 0" }}>{d.item.label}: {dropText(d.reason, L)}</p>)}
          {report.kept.length > 0 && <p style={{ margin: "0.25rem 0" }}>{report.stoppedBy === "unauthorized"
            ? L("Your session ended. Sign in again, then send the rest.", "انتهت جلستك. سجّل الدخول مجددًا ثم أرسل الباقي.")
            : L("The server could not be reached. The rest are still waiting.", "تعذّر الوصول إلى الخادم. ما تبقى لا يزال بانتظار الإرسال.")}</p>}
        </div>)}
      <div className="card-grid" style={{ gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))" }}>
        <Kpi label={L("To approve", "للاعتماد")} value={counts?.approvals} />
        <Kpi label={L("Open tasks", "مهام مفتوحة")} value={counts?.tasks} />
        <Kpi label={L("Overdue", "متأخرة")} value={counts?.overdue} />
        <Kpi label={L("Unread", "غير مقروءة")} value={counts?.unreadNotifications} />
        <Kpi label={L("Activities", "أنشطة")} value={counts?.activities} />
      </div>
      <div className="card-grid" style={{ marginTop: "1.5rem" }}>
        <Card title={L("What do I need to approve?", "ما الذي أحتاج لاعتماده؟")}>
          <Table rows={approvals.filter((a) => !queuedIds.has(a.id))} empty={L("Nothing waiting.", "لا شيء بانتظارك.")} columns={[
            { label: L("Item", "البند"), render: (a) => a.entityType },
            { label: L("Due", "الاستحقاق"), render: (a) => fmtDate(a.dueAt) },
            { label: "", render: (a) => (<span style={{ display: "flex", gap: "0.25rem", flexWrap: "wrap" }}>
              <button className="btn" onClick={() => decide(a.id, true)}>{L("Approve", "اعتماد")}</button>
              <button className="btn btn-secondary" onClick={() => decide(a.id, false)}>{L("Reject", "رفض")}</button></span>) },
          ]} />
        </Card>
        <Card title={L("What do I need to do?", "ما الذي عليّ إنجازه؟")}>
          <Table rows={tasks.filter((t) => !queuedIds.has(t.id))} empty={L("No open tasks.", "لا مهام مفتوحة.")} columns={[
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
