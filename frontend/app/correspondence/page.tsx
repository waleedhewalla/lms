"use client";
import { useCallback, useEffect, useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { Chatter } from "../../components/Chatter";
import { useSession } from "../../components/PublicChrome";
import { attempt, Card, Field, fmtDate, Page, Table, useL, useTenant } from "../../components/Ui";
import {
  DIRECTIONS, PRIORITIES, SLIP_ACTIONS, STATUSES, eventLabel, letterActions, pageWindow, recipientsOf, registerQuery, replyDirection, replySubject, stepState, typeName,
  type Direction, type History, type RegisterPage, type RegisterRow, type TypeDef, type Unit,
} from "../../lib/correspondence";

type Person = { id: string; fullName: string };
type Recipient = { personId: string | null; displayName: string; isExternal: boolean; kind: "To" | "Cc" };
type Detail = {
  correspondence: { id: string; number: string; type: Direction; subject: string; content: string; status: string; priority: string; isConfidential: boolean; createdAt: string; authorId: string; typeCode?: string | null; targetUnitId?: string | null; amount?: number | null };
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

  // Financial approval limits (managers of workflows only)
  const [limits, setLimits] = useState<{ stepId: string; label: string; above: number | null }[]>([]);
  const [limitEdit, setLimitEdit] = useState<Record<string, string>>({});

  // Types, units and the workflow of the selected letter
  const [types, setTypes] = useState<TypeDef[]>([]);
  const [units, setUnits] = useState<Unit[]>([]);
  const [typeCode, setTypeCode] = useState("");
  const [targetUnit, setTargetUnit] = useState("");
  const [amount, setAmount] = useState("");
  const [history, setHistory] = useState<History | null>(null);
  const [mine, setMine] = useState<RegisterRow[]>([]);
  const [editing, setEditing] = useState(false);
  const [editSubject, setEditSubject] = useState("");
  const [editContent, setEditContent] = useState("");
  const [editAmount, setEditAmount] = useState("");
  const [closing, setClosing] = useState(false);
  const [reason, setReason] = useState("");
  const [notice, setNotice] = useState("");

  // Directive
  const [slipAction, setSlipAction] = useState<string>(SLIP_ACTIONS[0]);
  const [slipText, setSlipText] = useState("");
  const [slipDays, setSlipDays] = useState(5);
  const [slipTo, setSlipTo] = useState<Person | null>(null);
  const [reviewer, setReviewer] = useState<Person | null>(null);

  const perms = session?.perms ?? [];
  const canRead = perms.includes("correspondence:read");
  const canStudent = perms.includes("correspondence:student-request");
  const canCloseAny = perms.includes("records:manage");
  const canCreate = perms.includes("correspondence:create");
  const canWrite = canCreate || canStudent;
  const canManage = perms.includes("workflow:manage");
  const ar = L("en", "ar") === "ar";
  const canSeeConfidential = perms.includes("correspondence:confidential");
  const personId = session?.personId ?? null;

  const filter = { tenantId, year, type, status, q };
  const load = useCallback((from = 0) => attempt(setErr, async () => {
    if (!tenantId) return;
    // Someone without the general read permission (a student) sees only the letters they wrote.
    if (!canRead) { setMine(await api<RegisterRow[]>(`/api/correspondence/mine?tenantId=${tenantId}`)); return; }
    setPage(await api<RegisterPage>(`/api/correspondence/register?${registerQuery({ ...filter, skip: from, take: PAGE_SIZE })}`));
    setSkip(from);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [tenantId, year, type, status, q, canRead]);

  useEffect(() => { if (tenantId && session) load(0); }, [tenantId, session]);   // eslint-disable-line react-hooks/exhaustive-deps

  const loadLimits = useCallback(() => attempt(setErr, async () => {
    if (!tenantId || !canManage) return;
    const r = await api<{ levels: { stepId: string; label: string; above: number | null }[] }>(`/api/correspondence/financial-limits?tenantId=${tenantId}`).catch(() => null);
    setLimits(r?.levels ?? []);
    setLimitEdit(Object.fromEntries((r?.levels ?? []).map((l) => [l.stepId, l.above == null ? "" : String(l.above)])));
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }), [tenantId, canManage]);
  useEffect(() => { if (tenantId && session) loadLimits(); }, [tenantId, session]);   // eslint-disable-line react-hooks/exhaustive-deps

  const saveLimits = () => attempt(setErr, async () => {
    await api("/api/correspondence/financial-limits", { method: "PUT", body: JSON.stringify({
      tenantId, limits: limits.map((l) => ({ stepId: l.stepId, above: limitEdit[l.stepId]?.trim() ? Number(limitEdit[l.stepId]) : null })) }) });
    flash("Limits saved. Letters sent from now on follow them.", "حُفظت الحدود. تتبعها المراسلات المرسلة من الآن.");
    await loadLimits();
  });

  // The catalogue of types and the units letters can be addressed to (both open to anyone who may write).
  useEffect(() => {
    if (!tenantId || !canWrite) return;
    attempt(setErr, async () => {
      setTypes((await api<TypeDef[]>(`/api/correspondence/types?tenantId=${tenantId}`)).filter((t) => t.isActive));
      setUnits(await api<Unit[]>(`/api/correspondence/units?tenantId=${tenantId}`));
    });
  }, [tenantId, canWrite]);   // eslint-disable-line react-hooks/exhaustive-deps

  const open = (row: { id: string }) => attempt(setErr, async () => {
    const d = await api<Detail>(`/api/correspondence/${row.id}?tenantId=${tenantId}`);
    // Threads and directives belong to readers; the author of a letter always sees its path.
    const [t, s] = canRead ? await Promise.all([
      api<Thread>(`/api/correspondence/${row.id}/thread?tenantId=${tenantId}`),
      api<Slip[]>(`/api/correspondence/${row.id}/routing-slips?tenantId=${tenantId}`),
    ]) : [null, [] as Slip[]];
    const h = await api<History>(`/api/correspondence/${row.id}/history?tenantId=${tenantId}`).catch(() => null);
    setDetail(d); setThread(t); setSlips(s); setHistory(h); setReviewer(null); setEditing(false); setClosing(false); setReason("");
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
    setRecipients([]); setTypeCode(from?.typeCode ?? (!canCreate && types.length === 1 ? types[0].code : "")); setTargetUnit(""); setAmount("");
  };

  const create = () => attempt(setErr, async () => {
    if (!personId) throw new Error(L("Your account is not linked to a person in the directory, so it cannot author letters.", "حسابك غير مرتبط بشخص في الدليل، لذا لا يمكنه إنشاء مراسلات."));
    const created = await api<{ id: string; number: string }>("/api/correspondence", { method: "POST", body: JSON.stringify({
      tenantId, type: newType, subject, content, authorId: personId, priority, isConfidential: confidential, parentCorrespondenceId: parentId,
      typeCode: typeCode || null, targetUnitId: targetUnit || null, amount: amount ? Number(amount) : null,
      recipients: recipients.map((r) => ({ personId: r.personId, displayName: r.displayName, isExternal: r.isExternal, kind: r.kind })),
    }) });
    setComposing(false);
    await load(0);
    await open(created);
  });

  const typed = !!detail?.correspondence.typeCode;
  const flash = (en: string, arText: string) => setNotice(L(en, arText));
  const refresh = async (letter: { id: string }) => { await Promise.all([open(letter), load(skip)]); };

  // A typed letter follows its type's workflow; the server names a missing approver in the error.
  const submitTyped = () => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/correspondence/${detail.correspondence.id}/submit`, { method: "POST", body: JSON.stringify({ tenantId }) });
    flash("Sent. It is now with the first approver.", "أُرسلت. هي الآن عند أول جهة اعتماد.");
    await refresh(detail.correspondence);
  });
  const resubmit = () => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/correspondence/${detail.correspondence.id}/resubmit`, { method: "POST", body: JSON.stringify({ tenantId }) });
    flash("Sent again from the first step.", "أُعيد إرسالها من الخطوة الأولى.");
    await refresh(detail.correspondence);
  });
  const startEdit = () => {
    if (!detail) return;
    setEditSubject(detail.correspondence.subject); setEditContent(detail.correspondence.content);
    setEditAmount(detail.correspondence.amount != null ? String(detail.correspondence.amount) : ""); setEditing(true);
  };
  const saveEdit = () => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/correspondence/${detail.correspondence.id}`, { method: "PATCH", body: JSON.stringify({
      tenantId, subject: editSubject, content: editContent, amount: editAmount ? Number(editAmount) : null }) });
    flash("Saved.", "تم الحفظ.");
    await refresh(detail.correspondence);
  });
  const closeLetter = () => attempt(setErr, async () => {
    if (!detail) return;
    await api(`/api/correspondence/${detail.correspondence.id}/close`, { method: "POST", body: JSON.stringify({ tenantId, reason: reason.trim() }) });
    flash("Closed.", "أُغلقت.");
    await refresh(detail.correspondence);
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

  const chosenType = types.find((t) => t.code === typeCode);
  const isStudentType = chosenType?.createPermission === "correspondence:student-request";
  const win = page ? pageWindow(page) : null;
  const c = detail?.correspondence;

  return (
    <Page title={L("Correspondence", "المراسلات")} code="C03" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<>
        <button type="button" className="btn btn-secondary" onClick={exportCsv} disabled={!tenantId}>{L("Export register (CSV)", "تصدير السجل (CSV)")}</button>
        {canWrite && <button type="button" className="btn" onClick={() => startCompose()} disabled={!tenantId}>{L("+ New letter", "+ مراسلة جديدة")}</button>}
      </>}>

      {session && !personId && (
        <p role="note" style={{ opacity: 0.8 }}>{L("Your account is not linked to a person in the directory: you can read the register but not write letters.", "حسابك غير مرتبط بشخص في الدليل: يمكنك قراءة السجل لكن لا يمكنك كتابة مراسلات.")}</p>)}

      {notice && <p role="status" style={{ color: "#16a34a" }}>{notice}</p>}

      {composing && (
        <Card title={parentId ? L("Reply", "رد") : L("New letter", "مراسلة جديدة")} wide>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            {types.length > 0 && !parentId && (
              <Field label={canCreate ? L("Letter type", "نوع المراسلة") : L("Student service", "الخدمة المطلوبة")}>
                <select className="input" value={typeCode} onChange={(e) => { setTypeCode(e.target.value); setNewType("Internal"); }}>
                  {canCreate && <option value="">{L("General (choose a reviewer yourself)", "عامة (تختار المراجع بنفسك)")}</option>}
                  {!typeCode && !canCreate && <option value="">{L("Choose a service", "اختر الخدمة")}</option>}
                  {types.map((t) => <option key={t.code} value={t.code}>{typeName(t, ar)}</option>)}
                </select>
              </Field>)}
            {chosenType && (
              <div role="note" style={{ opacity: 0.85, fontSize: "0.9rem" }}>
                <div>{chosenType.purpose}</div>
                <div>{L("From:", "من:")} {chosenType.senderDescription} · {L("To:", "إلى:")} {chosenType.receiverDescription}</div>
              </div>)}
            {chosenType?.requiresTargetUnit && (
              <Field label={L("Receiving unit", "الجهة المستقبلة")}>
                <select className="input" value={targetUnit} onChange={(e) => setTargetUnit(e.target.value)}>
                  <option value="">{L("Choose a unit", "اختر الجهة")}</option>
                  {units.map((u) => <option key={u.id} value={u.id}>{u.name} ({u.code})</option>)}
                </select>
              </Field>)}
            {chosenType?.requiresAmount && (
              <Field label={L("Amount", "المبلغ")}><input className="input" type="number" min={0} step="0.01" value={amount} onChange={(e) => setAmount(e.target.value)} /></Field>)}
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: "0.75rem" }}>
              {!chosenType && <Field label={L("Direction", "الاتجاه")}>
                <select className="input" value={newType} onChange={(e) => setNewType(e.target.value as Direction)}>
                  {DIRECTIONS.map((d) => <option key={d} value={d}>{d === "Incoming" ? L("Incoming (وارد)", "وارد") : d === "Outgoing" ? L("Outgoing (صادر)", "صادر") : L("Internal", "داخلي")}</option>)}
                </select>
              </Field>}
              <Field label={L("Priority", "الأولوية")}>
                <select className="input" value={priority} onChange={(e) => setPriority(e.target.value)}>{PRIORITIES.map((p) => <option key={p}>{p}</option>)}</select>
              </Field>
              {canSeeConfidential && !isStudentType && (
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
              <button type="button" className="btn" onClick={create}
                disabled={!subject.trim() || !content.trim() || !personId || (chosenType?.requiresTargetUnit && !targetUnit) || (chosenType?.requiresAmount && !(Number(amount) > 0)) || (!canCreate && !typeCode)}>{L("Save draft", "حفظ كمسودة")}</button>
              <button type="button" className="btn btn-secondary" onClick={() => setComposing(false)}>{L("Cancel", "إلغاء")}</button>
            </div>
          </div>
        </Card>)}

      {!canRead && (
        <Card title={L("My letters", "مراسلاتي")} wide>
          <Table<RegisterRow> rows={mine} empty={L("You have no letters yet.", "ليست لديك مراسلات بعد.")} onRowClick={open}
            columns={[
              { label: L("Number", "الرقم"), render: (r) => <strong>{r.number}</strong> },
              { label: L("Subject", "الموضوع"), render: (r) => r.subject },
              { label: L("Status", "الحالة"), render: (r) => r.status },
              { label: L("Date", "التاريخ"), render: (r) => fmtDate(r.createdAt) },
            ]} />
        </Card>)}

      {canRead && <Card title={L("Register", "السجل")} wide>
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
      </Card>}

      {canManage && limits.length > 0 && (
        <details style={{ margin: "1rem 0" }}>
          <summary><strong>{L("Financial approval limits", "حدود الاعتماد المالي")}</strong></summary>
          <p style={{ opacity: 0.8 }}>{L("For each approval level, the amount above which that level must also approve a financial letter. Leave empty for a level that always approves.", "لكل مستوى اعتماد: المبلغ الذي يلزم بعده اعتماد هذا المستوى للمراسلة المالية. اتركه فارغاً للمستوى الذي يعتمد دائماً.")}</p>
          <Table<(typeof limits)[number]> rows={limits} empty="" columns={[
            { label: L("Level", "المستوى"), render: (l) => l.label },
            { label: L("Also approves above (amount)", "يعتمد أيضاً بعد مبلغ"), render: (l) => (
              <input className="input" type="number" min={0} step="1" aria-label={l.label} value={limitEdit[l.stepId] ?? ""}
                onChange={(e) => setLimitEdit({ ...limitEdit, [l.stepId]: e.target.value })} />) },
          ]} />
          <div style={{ marginTop: "0.5rem" }}><button type="button" className="btn" onClick={saveLimits}>{L("Save limits", "حفظ الحدود")}</button></div>
        </details>)}

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
            {canCreate && canRead && <button type="button" className="btn btn-secondary" onClick={() => startCompose(c)}>{L("Reply", "رد")}</button>}
          </div>

          {c.status === "Draft" && canCreate && !typed && (
            <div style={{ margin: "1rem 0" }}>
              <strong>{L("Send for review", "إرسال للمراجعة")}</strong>
              <p style={{ opacity: 0.75, margin: "0.25rem 0" }}>{L("The reviewer gets an approval and a task; they decide in Approvals.", "يصل المراجع طلب موافقة ومهمة، ويقرر من صفحة الموافقات.")}</p>
              {reviewer
                ? <p>{L("Reviewer:", "المراجع:")} <strong>{reviewer.fullName}</strong> <button type="button" className="btn" onClick={submitForReview}>{L("Submit", "إرسال")}</button> <button type="button" className="btn btn-secondary" onClick={() => setReviewer(null)}>{L("Change", "تغيير")}</button></p>
                : <PersonPicker tenantId={tenantId} label={L("Find the reviewer", "ابحث عن المراجع")} onPick={setReviewer} />}
            </div>)}

          {detail.approvals.length > 0 && !history && (
            <p>{L("Approvals:", "الموافقات:")} {detail.approvals.map((a) => `${a.status} (${L("due", "الاستحقاق")} ${fmtDate(a.dueAt)})`).join(" · ")}</p>)}

          {(() => {
            const a = letterActions(c, personId !== null && c.authorId === personId, canCloseAny);
            return (a.edit || a.submit || a.resubmit || a.close) && (
              <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap", margin: "1rem 0" }}>
                {a.submit && <button type="button" className="btn" onClick={submitTyped}>{L("Send", "إرسال")}</button>}
                {a.resubmit && <button type="button" className="btn" onClick={resubmit}>{L("Send again", "إعادة الإرسال")}</button>}
                {a.edit && <button type="button" className="btn btn-secondary" onClick={startEdit}>{L("Edit", "تعديل")}</button>}
                {a.close && <button type="button" className="btn btn-secondary" onClick={() => setClosing(!closing)}>{c.authorId === personId ? L("Withdraw", "سحب") : L("Close", "إغلاق")}</button>}
              </div>);
          })()}

          {c.status === "Returned" && (
            <p role="note" style={{ color: "#b45309" }}>{L("Returned for changes. Read the reviewer's note below, edit the letter, then send it again.", "أُعيدت للتعديل. اقرأ ملاحظة المراجع أدناه، عدّل المراسلة ثم أعد إرسالها.")}</p>)}

          {editing && (
            <div style={{ display: "grid", gap: "0.5rem", margin: "1rem 0" }}>
              <Field label={L("Subject", "الموضوع")}><input className="input" value={editSubject} onChange={(e) => setEditSubject(e.target.value)} maxLength={300} /></Field>
              <Field label={L("Content", "المحتوى")}><textarea className="input" rows={5} value={editContent} onChange={(e) => setEditContent(e.target.value)} /></Field>
              {types.find((t) => t.code === c.typeCode)?.requiresAmount && (
                <Field label={L("Amount", "المبلغ")}><input className="input" type="number" min={0} step="0.01" value={editAmount} onChange={(e) => setEditAmount(e.target.value)} /></Field>)}
              <div style={{ display: "flex", gap: "0.5rem" }}>
                <button type="button" className="btn" onClick={saveEdit} disabled={!editSubject.trim() || !editContent.trim()}>{L("Save", "حفظ")}</button>
                <button type="button" className="btn btn-secondary" onClick={() => setEditing(false)}>{L("Cancel", "إلغاء")}</button>
              </div>
            </div>)}

          {closing && (
            <div style={{ display: "grid", gap: "0.5rem", margin: "1rem 0" }}>
              <Field label={L("Reason (required)", "السبب (مطلوب)")}><input className="input" value={reason} onChange={(e) => setReason(e.target.value)} maxLength={500} /></Field>
              <div><button type="button" className="btn" onClick={closeLetter} disabled={reason.trim().length < 3}>{L("Confirm", "تأكيد")}</button></div>
            </div>)}

          {history && history.steps.length > 0 && (
            <div style={{ margin: "1rem 0" }}>
              <strong>{L("Path of the letter", "مسار المراسلة")}</strong>
              <Table<History["steps"][number]> rows={history.steps} empty="" columns={[
                { label: L("Step", "الخطوة"), render: (_s) => history.steps.indexOf(_s) + 1 },
                { label: L("With", "عند"), render: (st) => st.assigneeName ?? "—" },
                { label: L("State", "الحالة"), render: (st) => {
                  const state = stepState(st, new Date());
                  const text = { Done: L("Approved", "تم الاعتماد"), Rejected: c.status === "Returned" ? L("Returned", "أُعيدت") : L("Rejected", "مرفوضة"), Cancelled: L("Cancelled", "أُلغيت"), Overdue: L("Overdue", "متأخرة"), Waiting: L("Waiting", "بالانتظار") }[state];
                  return <span style={state === "Overdue" ? { color: "#dc2626", fontWeight: 600 } : undefined}>{text}</span>;
                } },
                { label: L("Due", "الاستحقاق"), render: (st) => fmtDate(st.dueAt) },
                { label: L("Decided", "القرار"), render: (st) => (st.decidedAt ? `${st.decidedByName ?? ""} ${fmtDate(st.decidedAt)}` : "—") },
                { label: L("Note", "الملاحظة"), render: (st) => st.comment ?? "—" },
              ]} />
            </div>)}

          {history && history.events.length > 0 && (
            <details style={{ margin: "1rem 0" }}>
              <summary><strong>{L("History", "السجل التاريخي")}</strong></summary>
              <ul style={{ margin: "0.5rem 0", paddingInlineStart: "1.25rem" }}>
                {history.events.map((e, i) => (
                  <li key={i}>{fmtDate(e.at)} · {eventLabel(e.action, ar)}{e.actorName ? ` · ${e.actorName}` : ""}{e.details && !e.details.startsWith("INT-") ? ` — ${e.details}` : ""}</li>))}
              </ul>
            </details>)}

          {canRead && <div style={{ margin: "1rem 0" }}>
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
          </div>}

          {canRead && personId && <Chatter entityType="Correspondence" entityId={c.id} tenantId={tenantId} currentPersonId={personId} />}
        </Card>)}
    </Page>
  );
}
