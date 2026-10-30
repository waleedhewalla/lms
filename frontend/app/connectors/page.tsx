"use client";
import { useCallback, useEffect, useState } from "react";
import { apiBase, authHeaders } from "../../lib/api";
import {
  HELP, MAX_UPLOAD_BYTES, SYSTEMS, canCreate, errorText, isLive, optionsFor,
  type Connector, type CoursePage, type System,
} from "../../lib/connectors";
import { useSession } from "../../components/PublicChrome";
import { Card, Field, Page, Table, attempt, fmtDay, fmtDate, useL, useTenant } from "../../components/Ui";

const PAGE_SIZE = 50;

/** One call to the API that keeps the server's own explanation of a failure (the shared helper shows only the status). */
async function call<T>(path: string, init?: RequestInit): Promise<{ ok: boolean; status: number; body: T | { error?: string } | null }> {
  const res = await fetch(`${apiBase()}${path}`, { ...init, headers: { ...authHeaders(), ...(init?.headers ?? {}) } });
  const text = await res.text();
  let body: T | { error?: string } | null = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = null; }
  return { ok: res.ok, status: res.status, body };
}

export default function ConnectorsPage() {
  const L = useL();
  const session = useSession();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [note, setNote] = useState("");
  const [notEnabled, setNotEnabled] = useState(false);
  const [items, setItems] = useState<Connector[]>([]);
  const [busy, setBusy] = useState("");

  // Add a connection
  const [system, setSystem] = useState<System>("Moodle");
  const [name, setName] = useState("");
  const [baseUrl, setBaseUrl] = useState("");
  const [credential, setCredential] = useState("");
  const [opts, setOpts] = useState<Record<string, string>>({});

  // Course catalogue
  const [q, setQ] = useState("");
  const [connectorId, setConnectorId] = useState("");
  const [activeOnly, setActiveOnly] = useState(true);
  const [skip, setSkip] = useState(0);
  const [courses, setCourses] = useState<CoursePage | null>(null);

  const canManage = (session?.perms ?? []).some((p) => ["integration:manage", "integration:admin", "integration:write"].includes(p));
  const help = HELP[system];

  const loadConnectors = useCallback(async () => {
    if (!tenantId) return;
    const r = await call<Connector[]>(`/api/connectors?tenantId=${tenantId}`);
    if (r.status === 404) { setNotEnabled(true); setItems([]); return; }
    if (!r.ok) throw new Error(errorText(r.body, `Could not load connections (${r.status}).`));
    setNotEnabled(false);
    setItems(r.body as Connector[]);
  }, [tenantId]);

  const loadCourses = useCallback(async (from = 0) => {
    if (!tenantId || notEnabled) return;
    const p = new URLSearchParams({ tenantId, skip: String(from), take: String(PAGE_SIZE) });
    if (q.trim()) p.set("q", q.trim());
    if (connectorId) p.set("connectorId", connectorId);
    if (activeOnly) p.set("active", "true");
    const r = await call<CoursePage>(`/api/connectors/courses?${p}`);
    if (!r.ok) throw new Error(errorText(r.body, `Could not load courses (${r.status}).`));
    setCourses(r.body as CoursePage);
    setSkip(from);
  }, [tenantId, notEnabled, q, connectorId, activeOnly]);

  const refresh = () => attempt(setErr, async () => { await loadConnectors(); await loadCourses(0); });
  useEffect(() => { if (tenantId) void refresh(); }, [tenantId]);   // eslint-disable-line react-hooks/exhaustive-deps

  const create = () => attempt(setErr, async () => {
    setNote("");
    const r = await call<Connector>("/api/connectors", { method: "POST", body: JSON.stringify({
      tenantId, system, name: name.trim(), baseUrl: isLive(system) ? baseUrl.trim() : null, credential: isLive(system) ? credential : null, options: optionsFor(system, opts) }) });
    if (!r.ok) throw new Error(errorText(r.body, `Could not create the connection (${r.status}).`));
    setName(""); setBaseUrl(""); setCredential(""); setOpts({});
    setNote(L("Connection saved. The credential is stored sealed and is not shown again.", "تم حفظ الاتصال. يُخزَّن الرمز مشفّرًا ولا يُعرض مرة أخرى."));
    await refresh();
  });

  const sync = (c: Connector) => attempt(setErr, async () => {
    setNote(""); setBusy(c.id);
    try {
      const r = await call<{ created: number; updated: number; deactivated: number; total: number }>(`/api/connectors/${c.id}/sync`, { method: "POST", body: JSON.stringify({ tenantId }) });
      if (!r.ok) { await loadConnectors(); throw new Error(errorText(r.body, `Sync failed (${r.status}).`)); }
      const s = r.body as { created: number; updated: number; deactivated: number; total: number };
      setNote(`${c.name}: ${s.total} ${L("course(s)", "مقرر")} — ${s.created} ${L("new", "جديد")}, ${s.updated} ${L("updated", "محدّث")}, ${s.deactivated} ${L("no longer listed", "لم يعد مدرجًا")}.`);
      await refresh();
    } finally { setBusy(""); }
  });

  const upload = (c: Connector, file: File | undefined) => attempt(setErr, async () => {
    if (!file) return;
    setNote("");
    if (file.size > MAX_UPLOAD_BYTES) throw new Error(L("The file is larger than 5 MB.", "الملف أكبر من 5 ميغابايت."));
    setBusy(c.id);
    try {
      const r = await call<{ created: number; updated: number; deactivated: number; total: number }>(`/api/connectors/${c.id}/import`, { method: "POST", body: JSON.stringify({ tenantId, csv: await file.text() }) });
      if (!r.ok) throw new Error(errorText(r.body, `Import failed (${r.status}).`));
      const s = r.body as { created: number; updated: number; deactivated: number; total: number };
      setNote(`${c.name}: ${s.total} ${L("course(s)", "مقرر")} — ${s.created} ${L("new", "جديد")}, ${s.updated} ${L("updated", "محدّث")}, ${s.deactivated} ${L("no longer listed", "لم يعد مدرجًا")}.`);
      await refresh();
    } finally { setBusy(""); }
  });

  const remove = (c: Connector) => attempt(setErr, async () => {
    if (!window.confirm(L(`Delete "${c.name}" and the ${c.courses} course(s) it brought in?`, `حذف «${c.name}» وما جلبه من مقررات (${c.courses})؟`))) return;
    const r = await call<unknown>(`/api/connectors/${c.id}?tenantId=${tenantId}`, { method: "DELETE" });
    if (!r.ok) throw new Error(errorText(r.body, `Could not delete (${r.status}).`));
    if (connectorId === c.id) setConnectorId("");
    await refresh();
  });

  const win = courses ? { from: courses.total === 0 ? 0 : skip + 1, to: Math.min(skip + PAGE_SIZE, courses.total) } : null;

  return (
    <Page title={L("Connected systems", "الأنظمة المرتبطة")} code="C25" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button type="button" className="btn" onClick={refresh} disabled={!tenantId}>{L("Refresh", "تحديث")}</button>}>
      {notEnabled ? (
        <Card title={L("Not turned on", "غير مفعّل")} wide>
          <p>{L("Connected systems are not enabled on this server. An administrator turns them on with Connectors__Enabled (and a sealing key); see the deployment guide.", "الأنظمة المرتبطة غير مفعّلة على هذا الخادم. يفعّلها مدير النظام عبر Connectors__Enabled (مع مفتاح التشفير)؛ راجع دليل النشر.")}</p>
        </Card>
      ) : (<>
        {note && <p role="status" style={{ marginTop: 0 }}>{note}</p>}
        <p style={{ opacity: 0.8, marginTop: 0 }}>{L(
          "Brings in the course list only: code, title, term, dates, a head count where the system gives one, and instructor names. No students, grades or submissions are read.",
          "يجلب قائمة المقررات فقط: الرمز والعنوان والفصل والتواريخ وعدد الطلاب إن وفّره النظام وأسماء المدرّسين. لا تُقرأ بيانات الطلاب أو الدرجات أو التسليمات.")}</p>

        <div className="card-grid">
          <Card title={L("Connections", "الاتصالات")} wide>
            <Table<Connector> rows={items} empty={tenantId ? L("No connections yet.", "لا توجد اتصالات بعد.") : L("Enter a tenant above.", "أدخل المؤسسة أعلاه.")} columns={[
              { label: L("Name", "الاسم"), render: (c) => <strong>{c.name}</strong> },
              { label: L("System", "النظام"), render: (c) => c.system === "Csv" ? L("File (CSV)", "ملف (CSV)") : c.system },
              { label: L("Address", "العنوان"), render: (c) => c.baseUrl ?? "—" },
              { label: L("Last read", "آخر قراءة"), render: (c) => c.lastSyncAt
                ? <span>{c.lastSyncStatus === "Failed" ? "⚠ " : ""}{fmtDate(c.lastSyncAt)}<br /><small style={{ opacity: 0.8 }}>{c.lastSyncMessage}</small></span> : "—" },
              { label: L("Courses", "المقررات"), render: (c) => c.courses },
              { label: "", render: (c) => !canManage ? null : (
                <span style={{ display: "flex", gap: "0.25rem", flexWrap: "wrap" }}>
                  {c.system === "Csv"
                    ? <label className="btn btn-secondary" style={{ cursor: "pointer" }}>{busy === c.id ? "…" : L("Upload CSV", "رفع CSV")}
                        <input type="file" accept=".csv,text/csv" hidden disabled={busy === c.id} aria-label={`${L("Upload CSV for", "رفع CSV لـ")} ${c.name}`}
                          onChange={(e) => { const f = e.target.files?.[0]; e.target.value = ""; void upload(c, f); }} /></label>
                    : <button type="button" className="btn" onClick={() => sync(c)} disabled={busy === c.id}>{busy === c.id ? L("Reading…", "جارٍ القراءة…") : L("Read now", "اقرأ الآن")}</button>}
                  <button type="button" className="btn btn-secondary" onClick={() => remove(c)} disabled={busy === c.id}>{L("Delete", "حذف")}</button>
                </span>) },
            ]} />
          </Card>

          {canManage && (
            <Card title={L("Add a connection", "إضافة اتصال")}>
              <div style={{ display: "grid", gap: "0.75rem" }}>
                <Field label={L("System", "النظام")}>
                  <select className="input" value={system} onChange={(e) => { setSystem(e.target.value as System); setOpts({}); setCredential(""); }}>
                    {SYSTEMS.map((s) => <option key={s} value={s}>{s === "Csv" ? L("Other system (upload a CSV)", "نظام آخر (رفع ملف CSV)") : s}</option>)}
                  </select>
                </Field>
                <Field label={L("Name", "الاسم")}><input className="input" value={name} maxLength={100} onChange={(e) => setName(e.target.value)} /></Field>
                {isLive(system) && (<>
                  <Field label={L("Server address", "عنوان الخادم")}><input className="input" placeholder="https://" value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} /></Field>
                  <Field label={help ? L(help.credential.en, help.credential.ar) : ""}>
                    <input className="input" type="password" autoComplete="off" value={credential} onChange={(e) => setCredential(e.target.value)} />
                  </Field>
                  {(help?.options ?? []).map((o) => (
                    <Field key={o.key} label={L(o.en, o.ar)}>
                      <input className="input" placeholder={o.placeholder} value={opts[o.key] ?? ""} onChange={(e) => setOpts({ ...opts, [o.key]: e.target.value })} />
                    </Field>))}
                  <p style={{ margin: 0, fontSize: "0.85rem", opacity: 0.8 }}>{L("Use an account with the least rights the system allows (read access to the course list). The credential is sealed when saved and never shown again.", "استخدم حسابًا بأقل الصلاحيات التي يتيحها النظام (قراءة قائمة المقررات). يُشفَّر الرمز عند الحفظ ولا يُعرض مرة أخرى.")}</p>
                </>)}
                {!isLive(system) && <p style={{ margin: 0, fontSize: "0.85rem", opacity: 0.8 }}>{L("Then upload an export with a header row: code, title (required); externalId, term, startsOn, endsOn (yyyy-MM-dd), enrolled, instructors (separated by ;).", "ثم ارفع ملف تصدير بصف عناوين: code و title (مطلوبان)؛ و externalId و term و startsOn و endsOn (yyyy-MM-dd) و enrolled و instructors (مفصولة بـ ;).")}</p>}
                <button type="button" className="btn" onClick={create} disabled={!tenantId || !canCreate(system, name, baseUrl, credential)}>{L("Save connection", "حفظ الاتصال")}</button>
              </div>
            </Card>)}

          <Card title={L("Course catalogue", "فهرس المقررات")} wide>
            <form onSubmit={(e) => { e.preventDefault(); void attempt(setErr, () => loadCourses(0)); }}
              style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: "0.75rem", marginBottom: "1rem", alignItems: "end" }}>
              <Field label={L("Search code, title or instructor", "بحث بالرمز أو العنوان أو المدرّس")}><input className="input" value={q} onChange={(e) => setQ(e.target.value)} /></Field>
              <Field label={L("Connection", "الاتصال")}>
                <select className="input" value={connectorId} onChange={(e) => setConnectorId(e.target.value)}>
                  <option value="">{L("All", "الكل")}</option>{items.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </Field>
              <label style={{ display: "flex", gap: "0.5rem", alignItems: "center" }}><input type="checkbox" checked={activeOnly} onChange={(e) => setActiveOnly(e.target.checked)} />{L("Only courses still listed", "المقررات المدرجة حاليًا فقط")}</label>
              <button type="submit" className="btn" disabled={!tenantId}>{L("Apply", "تطبيق")}</button>
            </form>
            <Table<CoursePage["items"][number]> rows={courses?.items ?? []} empty={L("No courses.", "لا توجد مقررات.")} columns={[
              { label: L("Code", "الرمز"), render: (c) => <strong>{c.code}</strong> },
              { label: L("Title", "العنوان"), render: (c) => c.title },
              { label: L("Term", "الفصل"), render: (c) => c.term ?? "—" },
              { label: L("Dates", "التواريخ"), render: (c) => c.startsOn || c.endsOn ? `${c.startsOn ? fmtDay(c.startsOn) : "…"} → ${c.endsOn ? fmtDay(c.endsOn) : "…"}` : "—" },
              { label: L("Enrolled", "المسجّلون"), render: (c) => c.enrolledCount ?? "—" },
              { label: L("Instructors", "المدرّسون"), render: (c) => c.instructorNames || "—" },
              { label: L("Status", "الحالة"), render: (c) => c.isActive ? L("Listed", "مدرج") : L("No longer listed", "لم يعد مدرجًا") },
            ]} />
            {courses && win && (
              <div style={{ display: "flex", gap: "0.5rem", alignItems: "center", marginTop: "0.75rem" }}>
                <button type="button" className="btn btn-secondary" disabled={skip === 0} onClick={() => attempt(setErr, () => loadCourses(Math.max(0, skip - PAGE_SIZE)))}>{L("Previous", "السابق")}</button>
                <span>{win.from}–{win.to} {L("of", "من")} {courses.total}</span>
                <button type="button" className="btn btn-secondary" disabled={skip + PAGE_SIZE >= courses.total} onClick={() => attempt(setErr, () => loadCourses(skip + PAGE_SIZE))}>{L("Next", "التالي")}</button>
              </div>)}
          </Card>
        </div>
      </>)}
    </Page>
  );
}
