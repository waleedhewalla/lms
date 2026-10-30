"use client";
import { useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { feedUrl, isInsecure, webcalUrl, type FeedStatus } from "../../lib/calendarFeed";
import { useSession } from "../../components/PublicChrome";
import { attempt, Card, Field, fmtDate, HijriHint, Page, Table, useL, useTenant } from "../../components/Ui";

type Item = { id: string; title: string; kind: string; startsAt: string; endsAt: string; location: string | null; sourceType: string };
const KINDS = ["Meeting", "Deadline", "PolicyReview", "Exam", "Training", "Holiday", "Other"];

export default function CalendarPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [items, setItems] = useState<Item[]>([]);
  const [title, setTitle] = useState("");
  const [kind, setKind] = useState("Exam");
  const [starts, setStarts] = useState("");
  const [ends, setEnds] = useState("");
  const [location, setLocation] = useState("");
  const session = useSession();
  const [feed, setFeed] = useState<FeedStatus | null>(null);
  const [link, setLink] = useState("");   // the subscription link, shown once right after it is made
  const [copied, setCopied] = useState(false);

  const loadFeed = async () => { if (session?.personId) setFeed(await api<FeedStatus>(`/api/calendar/feed-token?tenantId=${tenantId}`)); };
  const load = () => attempt(setErr, async () => {
    setItems(await api<Item[]>(`/api/calendar/events?tenantId=${tenantId}`));
    await loadFeed();
  });
  const issueFeed = () => attempt(setErr, async () => {
    const r = await api<{ path: string; expiresAt: string }>("/api/calendar/feed-token", { method: "POST", body: JSON.stringify({ tenantId }) });
    setLink(feedUrl(apiBase(), r.path)); setCopied(false);
    await loadFeed();
  });
  const revokeFeed = () => attempt(setErr, async () => {
    await api(`/api/calendar/feed-token?tenantId=${tenantId}`, { method: "DELETE" });
    setLink("");
    await loadFeed();
  });
  const copyLink = () => attempt(setErr, async () => { await navigator.clipboard.writeText(link); setCopied(true); });
  const create = () => attempt(setErr, async () => {
    await api("/api/calendar/events", { method: "POST", body: JSON.stringify({ tenantId, title, kind,
      startsAt: new Date(starts).toISOString(), endsAt: new Date(ends || starts).toISOString(), location: location || null }) });
    setTitle(""); setLocation("");
    await load();
  });
  const remove = (id: string) => attempt(setErr, async () => {
    await api(`/api/calendar/events/${id}?tenantId=${tenantId}`, { method: "DELETE" });
    await load();
  });
  const downloadIcs = () => attempt(setErr, async () => {
    const res = await fetch(`${apiBase()}/api/calendar/export.ics?tenantId=${tenantId}`, { headers: authHeaders() });
    if (!res.ok) throw new Error(`export.ics → ${res.status}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a");
    a.href = url; a.download = "edunexus-calendar.ics"; a.click();
    URL.revokeObjectURL(url);
  });

  return (
    <Page title={L("Institutional Calendar", "التقويم المؤسسي")} code="UX-CAL-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<>
        <button className="btn" onClick={load}>{L("Load", "تحميل")}</button>
        <button className="btn btn-secondary" onClick={downloadIcs}>{L("Download .ics", "تنزيل ‎.ics")}</button></>}>
      <div className="card-grid">
        <Card title={L("Next 90 days", "الأيام التسعون القادمة")} wide>
          <Table rows={items} empty={L("Nothing scheduled.", "لا شيء مجدول.")} columns={[
            { label: L("When", "الموعد"), render: (i) => fmtDate(i.startsAt) },
            { label: L("What", "الحدث"), render: (i) => i.title },
            { label: L("Kind", "النوع"), render: (i) => <span className="badge">{i.kind}</span> },
            { label: L("Where", "المكان"), render: (i) => i.location ?? "–" },
            { label: "", render: (i) => i.sourceType === "CalendarEvent"
                ? <button className="btn btn-secondary" onClick={() => remove(i.id)}>{L("Delete", "حذف")}</button>
                : <span style={{ fontSize: "0.8rem", opacity: 0.7 }}>{i.sourceType}</span> },
          ]} />
        </Card>
        <Card title={L("Subscribe on your phone or in Outlook", "الاشتراك في الجوال أو أوتلوك")}>
          {session && !session.personId ? (
            <p role="note" style={{ opacity: 0.8 }}>{L("Your account is not linked to a person in the directory, so it has no personal calendar.", "حسابك غير مرتبط بشخص في الدليل، لذا لا يوجد له تقويم شخصي.")}</p>
          ) : (
            <div style={{ display: "grid", gap: "0.75rem" }}>
              <p style={{ margin: 0, opacity: 0.8 }}>{L("A private link that keeps your phone or Outlook up to date with institutional dates, your committee meetings, your open tasks and the actions assigned to you.", "رابط خاص يُبقي جوالك أو أوتلوك محدّثاً بمواعيد المؤسسة واجتماعات لجانك ومهامك المفتوحة والإجراءات المسندة إليك.")}</p>
              {feed?.active && !link && (
                <p style={{ margin: 0 }}>{L("A link is active until", "الرابط فعّال حتى")} {fmtDate(feed.expiresAt)}{feed.lastUsedAt ? ` · ${L("last used", "آخر استخدام")} ${fmtDate(feed.lastUsedAt)}` : ""}. {L("For safety it is shown only once; make a new one to see it again.", "للأمان يظهر مرة واحدة فقط؛ أنشئ رابطاً جديداً لعرضه مرة أخرى.")}</p>)}
              {link && (
                <div>
                  <input className="input" readOnly aria-label={L("Your calendar link", "رابط تقويمك")} value={link} onFocus={(e) => e.currentTarget.select()} />
                  <div style={{ display: "flex", gap: "0.5rem", margin: "0.5rem 0", flexWrap: "wrap" }}>
                    <button type="button" className="btn btn-secondary" onClick={copyLink}>{copied ? L("Copied", "تم النسخ") : L("Copy link", "نسخ الرابط")}</button>
                    <a className="btn btn-secondary" href={webcalUrl(link)}>{L("Open in calendar app", "فتح في تطبيق التقويم")}</a>
                  </div>
                  <p role="note" style={{ margin: 0, fontSize: "0.85rem" }}>{L("Anyone with this link can read your calendar. Do not share it. It is shown only now.", "أي شخص لديه هذا الرابط يستطيع قراءة تقويمك. لا تشاركه. يظهر الآن فقط.")}</p>
                  {isInsecure(link) && <p role="alert" style={{ margin: "0.5rem 0 0", color: "#ef4444", fontSize: "0.85rem" }}>{L("This server does not use https, so the link can be read on the network. Use it only for testing.", "هذا الخادم لا يستخدم https، لذا يمكن قراءة الرابط على الشبكة. استخدمه للتجربة فقط.")}</p>}
                </div>)}
              <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}>
                <button type="button" className="btn" onClick={issueFeed} disabled={!tenantId || !session?.personId}>{feed?.active ? L("Make a new link", "إنشاء رابط جديد") : L("Create my link", "إنشاء رابطي")}</button>
                {feed?.active && <button type="button" className="btn btn-secondary" onClick={revokeFeed}>{L("Turn off", "إيقاف")}</button>}
              </div>
              {feed?.active && <p style={{ margin: 0, fontSize: "0.8rem", opacity: 0.7 }}>{L("A new link replaces the old one, which stops working.", "الرابط الجديد يحلّ محل القديم الذي يتوقف عن العمل.")}</p>}
            </div>)}
        </Card>
        <Card title={L("Add event", "إضافة حدث")}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <Field label={L("Title", "العنوان")}><input className="input" value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
            <Field label={L("Kind", "النوع")}>
              <select className="input" value={kind} onChange={(e) => setKind(e.target.value)}>{KINDS.map((k) => <option key={k}>{k}</option>)}</select></Field>
            <Field label={L("Starts", "البداية")}><input className="input" type="datetime-local" value={starts} onChange={(e) => setStarts(e.target.value)} /><HijriHint value={starts} /></Field>
            <Field label={L("Ends", "النهاية")}><input className="input" type="datetime-local" value={ends} onChange={(e) => setEnds(e.target.value)} /><HijriHint value={ends} /></Field>
            <Field label={L("Location", "المكان")}><input className="input" value={location} onChange={(e) => setLocation(e.target.value)} /></Field>
            <button className="btn" disabled={!title || !starts} onClick={create}>{L("Add", "إضافة")}</button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
