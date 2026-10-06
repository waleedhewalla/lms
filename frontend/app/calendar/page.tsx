"use client";
import { useState } from "react";
import { API_BASE, api, authHeaders } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

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

  const load = () => attempt(setErr, async () => setItems(await api<Item[]>(`/api/calendar/events?tenantId=${tenantId}`)));
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
    const res = await fetch(`${API_BASE}/api/calendar/export.ics?tenantId=${tenantId}`, { headers: authHeaders() });
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
        <Card title={L("Add event", "إضافة حدث")}>
          <div style={{ display: "grid", gap: "0.75rem" }}>
            <Field label={L("Title", "العنوان")}><input className="input" value={title} onChange={(e) => setTitle(e.target.value)} /></Field>
            <Field label={L("Kind", "النوع")}>
              <select className="input" value={kind} onChange={(e) => setKind(e.target.value)}>{KINDS.map((k) => <option key={k}>{k}</option>)}</select></Field>
            <Field label={L("Starts", "البداية")}><input className="input" type="datetime-local" value={starts} onChange={(e) => setStarts(e.target.value)} /></Field>
            <Field label={L("Ends", "النهاية")}><input className="input" type="datetime-local" value={ends} onChange={(e) => setEnds(e.target.value)} /></Field>
            <Field label={L("Location", "المكان")}><input className="input" value={location} onChange={(e) => setLocation(e.target.value)} /></Field>
            <button className="btn" disabled={!title || !starts} onClick={create}>{L("Add", "إضافة")}</button>
          </div>
        </Card>
      </div>
    </Page>
  );
}
