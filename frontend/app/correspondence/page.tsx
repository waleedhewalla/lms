"use client";
import { useCallback, useEffect, useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { Chatter } from "../../components/Chatter";
import { useSession } from "../../components/PublicChrome";
import { attempt, Card, Field, fmtDate, Page, Table, useL, useTenant } from "../../components/Ui";
import {
  DIRECTIONS, PRIORITIES, SLIP_ACTIONS, STATUSES, pageWindow, recipientsOf, registerQuery, replyDirection, replySubject,
  type Direction, type RegisterPage, type RegisterRow,
} from "../../lib/correspondence";

type Person = { id: string; fullName: string };
type Recipient = { personId: string | null; displayName: string; isExternal: boolean; kind: "To" | "Cc" };
type Detail = {
  correspondence: { id: string; number: string; type: Direction; subject: string; content: string; status: string; priority: string; isConfidential: boolean; createdAt: string };
  correspondents: { displayName: string; isExternal: boolean; kind: string }[];
  approvals: { id: string; status: string; assigneeId: string; dueAt: string }[];
};
type Thread = { current: { id: string }; root: { id: string; number: string; subject: string }; replies: { id: string; number: string; subject: string; status: string }[] };
type Slip = { id: string; actionRequired: string; instructions: string; toPersonId: string | null; dueAt: string | null; isCompleted: boolean };

const PAGE_SIZE = 25;
const thisYear = () => new Date().getFullYear();

/** Search the directory and pick one person (used for recipients, the reviewer and directives). */
function PersonPicker({ tenantId, label, onPick }: { tenantId: string; label: string; onPick: (p: Person) => void }) {
  const L = useL();
  const [q, setQ] = useState("");
  const [found, setFound] = useState<Person[]>([]);
  const [err, setErr] = useState("");
  const search = () => attempt(setErr, async () => setFound(await api<Person[]>(`/api/people?tenantId=${tenantId}&q=${encodeURIComponent(q)}`)));
  return (
    <div>
      <div style={{ display: "flex", gap: "0.5rem" }}>
        <input className="input" aria-label={label} placeholder={label} value={q} onChange={(e) => setQ(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); search(); } }} />
        <button type="button" className="btn btn-secondary" onClick={search} disabled={!tenantId}>{L("Search", "بحث")}</button>
      </div>
      {err && <p role="alert" style={{ color: "#ef4444", fontSize: "0.85rem" }}>{err}</p>}
      {found.length > 0 && (
        <ul style={{ listStyle: "none", padding: 0, margin: "0.5rem 0 0" }}>
          {found.slice(0, 8).map((p) => (
            <li key={p.id}><button type="button" className="btn btn-secondary" style={{ width: "100%", textAlign: "start", marginBottom: "0.25rem" }}
              onClick={() => { onPick(p); setFound([]); setQ(""); }}>{p.fullName}</button></li>
          ))}
        </ul>)}
    </div>
  );
}

export default function CorrespondencePage() {
  const L = useL();
  const session = useSession();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");

  // Register
  const [year, setYear] = useState(thisYear());
  const [type, setType] = useState("");
  const [status, setStatus] = useState("");
  const [q, setQ] = useState("");
  const [skip, setSkip] = useState(0);
  const [page, setPage] = useState<RegisterPage | null>(null);

  // Selected letter
  const [detail, setDetail] = useState<Detail | null>(null);
  const [thread, setThread] = useState<Thread | null>(null);
  const [slips, setSlips] = useState<Slip[]>([]);

  // New letter
  const [composing, setComposing] = useState(false);
  const [newType, setNewType] = useState<Direction>("Internal");
  const [subject, setSubject] = useState("");
  const [content, setContent] = useState("");
  const [priority, setPriority] = useState("Normal");
  const [confidential, setConfidential] = useState(false);
  const [recipients, setRecipients] = useState<Recipient[]>([]);
  const [externalName, setExternalName] = useState("");
  const [recipientKind, setRecipientKind] = useState<"To" | "Cc">("To");
  const [parentId, setParentId] = useState<string | null>(null);

  // Directive
  const [slipAction, setSlipAction] = useState<string>(SLIP_ACTIONS[0]);
  const [slipText, setSlipText] = useState("");
  const [slipDays, setSlipDays] = useState(5);
  const [slipTo, setSlipTo] = useState<Person | null>(null);
  const [reviewer, setReviewer] = useState<Person | null>(null);

  const perms = session?.perms ?? [];
  const canCreate = perms.includes("correspondence:create");
  const canSeeConfidential = perms.includes("correspondence:confidential");
  const personId = session?.personId ?? null;

  const filter = { tenantId, year, type, status, q };
  const load = useCallback((from = 0) => attempt(setErr, async () => {
    if (!tenantId) return;
    setPage(await api<RegisterPage>(`/api/correspondence/register?${registerQuery({ ...filter, skip: from, take: PAGE_SIZE })}`));
    setSkip(from);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [tenantId, year, type, status, q]);

  useEffect(() => { if (tenantId) load(0); }, [tenantId]);   // eslint-disable-line react-hooks/exhaustive-deps

  const open = (row: { id: string }) => attempt(setErr, async () => {
    const [d, t, s] = await Promise.all([
      api<Detail>(`/api/correspondence/${row.id}?tenantId=${tenantId}`),
      api<Thread>(`/api/correspondence/${row.id}/thread?tenantId=${tenantId}`),
      api<Slip[]>(`/api/correspondence/${row.id}/routing-slips?tenantId=${tenantId}`),
    ]);
    setDetail(d); setThread(t); setSlips(s); setReviewer(null);
  });

  const exportCsv = () => attempt(setErr, async () => {
    const res = await fetch(`${apiBase()}/api/correspondence/register/export?${registerQuery(filter, { calendar: "both" })}`, { headers: authHeaders() });
    if (!res.ok) throw new Error(`register export → ${res.status}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a");
    a.href = url; a.download = `correspondence-register-${year}.csv`; a.click();
    URL.revokeObjectURL(url);
  });

  const startCompose = (from?: Detail["correspondence"]) => {
    setComposing(true);
    setParentId(from?.id ?? null);
    setNewType(from ? replyDirection(from.type) : "Internal");
    setSubject(from ? replySubject(from.subject) : "");
    setContent(""); setPriority("Normal"); setConfidential(from?.isConfidential ?? false);
    setRecipients([]);
  };

  const create = () => attempt(setErr, async () => {
    if (!personId) throw new Error(L("Your account is not linked to a person in the directory, so it cannot author letters.", "حسابك غير مرتبط بشخص في الدليل، لذا لا يمكنه إنشاء مراسلات."));
    const created = await api<{ id: string; number: string }>("/api/correspondence", { method: "POST", body: JSON.stringify({
      tenantId, type: newType, subject, content, authorId: personId, priority, isConfidential: confidential, parentCorrespondenceId: parentId,
      recipients: recipients.map((r) => ({ personId: r.personId, displayName: r.displayName, isExternal: r.isExternal, kind: r.kind })),
    }) });
    setComposing(false);
    await load(0);
    await open(created);
  });

  const submitForReview = () => attempt(setErr, async () => {
    if (!detail || !reviewer) return;
    await api(`/api/correspondence/${detail.correspondence.id}/submit`, { method: "POST", body: JSON.stringify({ tenantId, reviewerId: reviewer.id }) });
    await Promise.all([open(detail.correspondence), load(skip)]);
  });

  const addDirective = () => attempt(setErr, async () => {
    if (!detail || !personId || !slipText.trim()) return;
    await api(`/api/correspondence/${detail.correspondence.id}/routing-slips`, { method: "POST", body: JSON.stringify({
      tenantId, fromPersonId: personId, toPersonId: slipTo?.id ?? null, actionRequired: slipAction, instructions: slipText.trim(),
      dueAt: new Date(Date.now() + slipDays * 86_400_000).toISOString(),
    }) });
    setSlipText(""); setSlipTo(null);
    await open(detail.correspondence);
  });

  const completeSlip = (slipId: string) => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/correspondence/${detail.correspondence.id}/routing-slips/${slipId}/complete?tenantId=${tenantId}`, { method: "POST" });
    await open(detail.correspondence);
  });

  const win = page ? pageWindow(page) : null;
  const c = detail?.correspondence;

  return (
    <Page title={L("Correspondence", "المراسلات")} code="C03" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<>
        <button type="button" className="btn btn-secondary" onClick={exportCsv} disabled={!tenantId}>{L("Export register (CSV)", "تصدير السجل (CSV)")}</button>
        {canCreate && <button type="button" className="btn" onClick={() => startCompose()} disabled={!tenantId}>{L("+ New letter", "+ مراسلة جديدة")}</button>}
      </>}>

      {session && !personId && (
        <p role="note" style={{ opacity: 0.8 }}>{L("Your account is not linked to a person in the directory: you can read the register but not write letters.", "حسابك غير مرتبط بشخص في الدليل: يمكنك قراءة السجل لكن لا يمكنك كتابة مراسلات.")}</p>)}

      {composing && (
        <Card title={parentId ? L("Reply", "رد") : L("New letter", "مراسلة جديدة")} wide>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: "0.75rem" }}>
              <Field label={L("Direction", "الاتجاه")}>
                <select className="input" value={newType} onChange={(e) => setNewType(e.target.value as Direction)}>
                  {DIRECTIONS.map((d) => <option key={d} value={d}>{d === "Incoming" ? L("Incoming (وارد)", "وارد") : d === "Outgoing" ? L("Outgoing (صادر)", "صادر") : L("Internal", "داخلي")}</option>)}
                </select>
              </Field>
              <Field label={L("Priority", "الأولوية")}>
                <select className="input" value={priority} onChange={(e) => setPriority(e.target.value)}>{PRIORITIES.map((p) => <option key={p}>{p}</option>)}</select>
              </Field>
              {canSeeConfidential && (
                <label style={{ display: "flex", alignItems: "center", gap: "0.5rem", alignSelf: "end" }}>
                  <input type="checkbox" checked={confidential} onChange={(e) => setConfidential(e.target.checked)} />{L("Confidential", "سري")}
                </label>)}
            </div>
            <Field label={L("Subject", "الموضوع")}><input className="input" value={subject} onChange={(e) => setSubject(e.target.value)} maxLength={300} /></Field>
            <Field label={L("Content", "المحتوى")}><textarea className="input" rows={5} value={content} onChange={(e) => setContent(e.target.value)} /></Field>

            <div>
              <strong>{L("Recipients", "المستلمون")}</strong>
              <div style={{ display: "flex", gap: "0.5rem", margin: "0.5rem 0", flexWrap: "wrap" }}>
                <select className="input" aria-label={L("To or copy", "إلى أو نسخة")} style={{ width: "auto" }} value={recipientKind} onChange={(e) => setRecipientKind(e.target.value as "To" | "Cc")}>
                  <option value="To">{L("To", "إلى")}</option><option value="Cc">{L("Cc (copy)", "نسخة إلى")}</option>
                </select>
                <input className="input" style={{ flex: 1, minWidth: "12rem" }} aria-label={L("External party name", "اسم جهة خارجية")} placeholder={L("External party (e.g. a ministry)", "جهة خارجية (مثل وزارة)")}
                  value={externalName} onChange={(e) => setExternalName(e.target.value)} />
                <button type="button" className="btn btn-secondary" disabled={!externalName.trim()} onClick={() => {
                  setRecipients([...recipients, { personId: null, displayName: externalName.trim(), isExternal: true, kind: recipientKind }]); setExternalName("");
                }}>{L("Add external", "إضافة جهة خارجية")}</button>
              </div>
              <PersonPicker tenantId={tenantId} label={L("Find a person in the directory", "ابحث عن شخص في الدليل")}
                onPick={(p) => setRecipients([...recipients, { personId: p.id, displayName: p.fullName, isExternal: false, kind: recipientKind }])} />
              {recipients.length > 0 && (
                <ul style={{ margin: "0.5rem 0 0", paddingInlineStart: "1.25rem" }}>
                  {recipients.map((r, i) => (
                    <li key={i}>{r.kind === "Cc" ? L("Cc: ", "نسخة: ") : ""}{r.displayName}{r.isExternal ? L(" (external)", " (خارجي)") : ""}{" "}
                      <button type="button" className="btn btn-secondary" style={{ padding: "0 0.5rem" }} aria-label={L("Remove", "إزالة")} onClick={() => setRecipients(recipients.filter((_, j) => j !== i))}>×</button></li>))}
                </ul>)}
            </div>

            <div style={{ display: "flex", gap: "0.5rem" }}>
              <button type="button" className="btn" onClick={create} disabled={!subject.trim() || !content.trim() || !personId}>{L("Save draft", "حفظ كمسودة")}</button>
              <button type="button" className="btn btn-secondary" onClick={() => setComposing(false)}>{L("Cancel", "إلغاء")}</button>
            </div>
          </div>
        </Card>)}

      <Card title={L("Register", "السجل")} wide>
        <form onSubmit={(e) => { e.preventDefault(); load(0); }} style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))", gap: "0.75rem", marginBottom: "1rem", alignItems: "end" }}>
          <Field label={L("Year", "السنة")}><input className="input" type="number" min={2000} max={2100} value={year} onChange={(e) => setYear(Number(e.target.value))} /></Field>
          <Field label={L("Direction", "الاتجاه")}>
            <select className="input" value={type} onChange={(e) => setType(e.target.value)}><option value="">{L("All", "الكل")}</option>{DIRECTIONS.map((d) => <option key={d}>{d}</option>)}</select>
          </Field>
          <Field label={L("Status", "الحالة")}>
            <select className="input" value={status} onChange={(e) => setStatus(e.target.value)}><option value="">{L("All", "الكل")}</option>{STATUSES.map((s) => <option key={s}>{s}</option>)}</select>
          </Field>
          <Field label={L("Search number or subject", "بحث بالرقم أو الموضوع")}><input className="input" value={q} onChange={(e) => setQ(e.target.value)} /></Field>
          <button type="submit" className="btn" disabled={!tenantId}>{L("Apply", "تطبيق")}</button>
        </form>
        <Table<RegisterRow> rows={page?.items ?? []} empty={tenantId ? L("No letters match.", "لا توجد مراسلات مطابقة.") : L("Enter a tenant above to load the register.", "أدخل المؤسسة أعلاه لتحميل السجل.")}
          onRowClick={open}
          columns={[
            { label: L("Number", "الرقم"), render: (r) => <strong>{r.number}</strong> },
            { label: L("Subject", "الموضوع"), render: (r) => <>{r.isConfidential ? "🔒 " : ""}{r.subject}</> },
            { label: L("Status", "الحالة"), render: (r) => r.status },
            { label: L("To", "إلى"), render: (r) => recipientsOf(r, "To").join("، ") || "—" },
            { label: L("Cc", "نسخة"), render: (r) => recipientsOf(r, "Cc").join("، ") || "—" },
            { label: L("Date", "التاريخ"), render: (r) => fmtDate(r.createdAt) },
          ]} />
        {page && win && (
          <div style={{ display: "flex", gap: "0.5rem", alignItems: "center", marginTop: "0.75rem" }}>
            <button type="button" className="btn btn-secondary" disabled={!win.hasPrev} onClick={() => load(Math.max(0, skip - PAGE_SIZE))}>{L("Previous", "السابق")}</button>
            <span>{win.from}–{win.to} {L("of", "من")} {page.total}</span>
            <button type="button" className="btn btn-secondary" disabled={!win.hasNext} onClick={() => load(skip + PAGE_SIZE)}>{L("Next", "التالي")}</button>
          </div>)}
      </Card>

      {c && detail && (
        <Card title={`${c.number} · ${c.subject}`} wide>
          <p style={{ opacity: 0.75 }}>{c.type} · {c.status} · {c.priority}{c.isConfidential ? ` · ${L("Confidential", "سري")}` : ""} · {fmtDate(c.createdAt)}</p>
          <p style={{ whiteSpace: "pre-wrap" }}>{c.content}</p>
          {detail.correspondents.length > 0 && (
            <p>{detail.correspondents.map((r) => `${r.kind === "Cc" ? L("Cc", "نسخة") : L("To", "إلى")}: ${r.displayName}`).join(" · ")}</p>)}

          {thread && (thread.root.id !== c.id || thread.replies.length > 0) && (
            <div style={{ margin: "1rem 0" }}>
              <strong>{L("Thread", "السلسلة")}</strong>
              <ul style={{ margin: "0.25rem 0", paddingInlineStart: "1.25rem" }}>
                {thread.root.id !== c.id && <li>{L("In reply to", "رداً على")} <button type="button" className="btn btn-secondary" style={{ padding: "0 0.5rem" }} onClick={() => open(thread.root)}>{thread.root.number} · {thread.root.subject}</button></li>}
                {thread.replies.map((r) => <li key={r.id}><button type="button" className="btn btn-secondary" style={{ padding: "0 0.5rem" }} onClick={() => open(r)}>{r.number} · {r.subject}</button> ({r.status})</li>)}
              </ul>
            </div>)}

          <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", margin: "1rem 0" }}>
            {canCreate && <button type="button" className="btn btn-secondary" onClick={() => startCompose(c)}>{L("Reply", "رد")}</button>}
          </div>

          {c.status === "Draft" && canCreate && (
            <div style={{ margin: "1rem 0" }}>
              <strong>{L("Send for review", "إرسال للمراجعة")}</strong>
              <p style={{ opacity: 0.75, margin: "0.25rem 0" }}>{L("The reviewer gets an approval and a task; they decide in Approvals.", "يصل المراجع طلب موافقة ومهمة، ويقرر من صفحة الموافقات.")}</p>
              {reviewer
                ? <p>{L("Reviewer:", "المراجع:")} <strong>{reviewer.fullName}</strong> <button type="button" className="btn" onClick={submitForReview}>{L("Submit", "إرسال")}</button> <button type="button" className="btn btn-secondary" onClick={() => setReviewer(null)}>{L("Change", "تغيير")}</button></p>
                : <PersonPicker tenantId={tenantId} label={L("Find the reviewer", "ابحث عن المراجع")} onPick={setReviewer} />}
            </div>)}

          {detail.approvals.length > 0 && (
            <p>{L("Approvals:", "الموافقات:")} {detail.approvals.map((a) => `${a.status} (${L("due", "الاستحقاق")} ${fmtDate(a.dueAt)})`).join(" · ")}</p>)}

          <div style={{ margin: "1rem 0" }}>
            <strong>{L("Directives (routing slips)", "التأشيرات والإحالات")}</strong>
            <Table<Slip> rows={slips} empty={L("No directives yet.", "لا توجد تأشيرات بعد.")} columns={[
              { label: L("Action", "الإجراء"), render: (s) => s.actionRequired },
              { label: L("Instructions", "التعليمات"), render: (s) => s.instructions },
              { label: L("Due", "الاستحقاق"), render: (s) => (s.dueAt ? fmtDate(s.dueAt) : "—") },
              { label: L("State", "الحالة"), render: (s) => (s.isCompleted ? L("Done", "منجزة") : canCreate
                ? <button type="button" className="btn btn-secondary" onClick={() => completeSlip(s.id)}>{L("Mark done", "تمت")}</button> : L("Open", "مفتوحة")) },
            ]} />
            {canCreate && personId && (
              <div style={{ display: "grid", gap: "0.5rem", marginTop: "0.75rem" }}>
                <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: "0.5rem" }}>
                  <Field label={L("Action required", "الإجراء المطلوب")}>
                    <select className="input" value={slipAction} onChange={(e) => setSlipAction(e.target.value)}>{SLIP_ACTIONS.map((a) => <option key={a}>{a}</option>)}</select>
                  </Field>
                  <Field label={L("Due in (days)", "المدة (أيام)")}><input className="input" type="number" min={1} max={365} value={slipDays} onChange={(e) => setSlipDays(Number(e.target.value))} /></Field>
                </div>
                {slipTo
                  ? <p>{L("Forward to:", "إحالة إلى:")} <strong>{slipTo.fullName}</strong> <button type="button" className="btn btn-secondary" onClick={() => setSlipTo(null)}>{L("Change", "تغيير")}</button></p>
                  : <PersonPicker tenantId={tenantId} label={L("Forward to (optional)", "إحالة إلى (اختياري)")} onPick={setSlipTo} />}
                <Field label={L("Instructions", "التعليمات")}><textarea className="input" rows={2} value={slipText} onChange={(e) => setSlipText(e.target.value)} /></Field>
                <div><button type="button" className="btn" onClick={addDirective} disabled={!slipText.trim()}>{L("Add directive", "إضافة تأشيرة")}</button></div>
              </div>)}
          </div>

          {personId && <Chatter entityType="Correspondence" entityId={c.id} tenantId={tenantId} currentPersonId={personId} />}
        </Card>)}
    </Page>
  );
}
