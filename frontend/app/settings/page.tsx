"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Page, Table, attempt, useL, useTenant } from "../../components/Ui";

type Me = { subject: string; tenantId: string | null; permissions: string[]; person: { id: string; fullName: string } | null };
type Pref = { channel: string; enabled: boolean; minPriority: string; quietFromHour: number | null; quietToHour: number | null };
type Sla = { id: string; entityType: string; responseHours: number; escalateAfterHours: number; isActive: boolean };
const PRIORITIES = ["FYI", "Normal", "Important", "Urgent", "Emergency"];

export default function SettingsPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [ok, setOk] = useState("");
  const [me, setMe] = useState<Me | null>(null);
  const [prefs, setPrefs] = useState<Pref[]>([]);
  const [slas, setSlas] = useState<Sla[]>([]);
  const [entityType, setEntityType] = useState("Request");
  const [hours, setHours] = useState(72);
  const [escalate, setEscalate] = useState(120);

  const load = () => attempt(setErr, async () => {
    setOk("");
    setMe(await api<Me>("/api/auth/me"));
    setPrefs(await api<Pref[]>(`/api/notifications/preferences?tenantId=${tenantId}`));
    setSlas(await api<Sla[]>(`/api/sla/policies?tenantId=${tenantId}`));
  });
  const update = (i: number, patch: Partial<Pref>) => setPrefs(prefs.map((p, j) => (j === i ? { ...p, ...patch } : p)));
  const hour = (v: string) => (v === "" ? null : Math.max(0, Math.min(23, Number(v))));
  const savePrefs = () => attempt(setErr, async () => {
    setPrefs(await api<Pref[]>("/api/notifications/preferences", { method: "PUT", body: JSON.stringify({ tenantId, preferences: prefs }) }));
    setOk(L("Preferences saved.", "تم حفظ التفضيلات."));
  });
  const saveSla = () => attempt(setErr, async () => {
    await api("/api/sla/policies", { method: "POST", body: JSON.stringify({ tenantId, entityType, responseHours: hours, escalateAfterHours: escalate, isActive: true }) });
    setSlas(await api<Sla[]>(`/api/sla/policies?tenantId=${tenantId}`));
  });

  return (
    <Page title={L("Settings", "الإعدادات")} code="UX-ADM-002" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      {ok && <p role="status" style={{ color: "var(--primary)" }}>{ok}</p>}
      <div className="card-grid">
        <Card title={L("My account", "حسابي")}>
          {!me ? <p style={{ opacity: 0.7 }}>{L("Load to see your account.", "حمّل لعرض حسابك.")}</p> : (<>
            <p style={{ marginTop: 0 }}>{L("Person", "الشخص")}: <strong>{me.person?.fullName ?? L("not linked", "غير مرتبط")}</strong></p>
            <p>{L("Permissions", "الصلاحيات")} ({me.permissions.length}):</p>
            <div style={{ display: "flex", flexWrap: "wrap", gap: "0.25rem" }}>{me.permissions.map((p) => <span key={p} className="badge">{p}</span>)}</div>
          </>)}
        </Card>
        <Card title={L("Notification preferences", "تفضيلات الإشعارات")}>
          <Table rows={prefs} empty={L("Load to edit.", "حمّل للتعديل.")} columns={[
            { label: L("Channel", "القناة"), render: (p) => p.channel },
            { label: L("On", "مفعّل"), render: (p) => (
              <input type="checkbox" checked={p.enabled} disabled={p.channel === "InApp"} onChange={(e) => update(prefs.indexOf(p), { enabled: e.target.checked })} />) },
            { label: L("Minimum priority", "أدنى أولوية"), render: (p) => (
              <select className="input" value={p.minPriority} onChange={(e) => update(prefs.indexOf(p), { minPriority: e.target.value })}>
                {PRIORITIES.map((x) => <option key={x}>{x}</option>)}</select>) },
            { label: L("Quiet from (UTC)", "هدوء من (UTC)"), render: (p) => (
              <input className="input" type="number" min={0} max={23} value={p.quietFromHour ?? ""} style={{ width: "5rem" }}
                onChange={(e) => update(prefs.indexOf(p), { quietFromHour: hour(e.target.value) })} />) },
            { label: L("to", "إلى"), render: (p) => (
              <input className="input" type="number" min={0} max={23} value={p.quietToHour ?? ""} style={{ width: "5rem" }}
                onChange={(e) => update(prefs.indexOf(p), { quietToHour: hour(e.target.value) })} />) },
          ]} />
          <p style={{ fontSize: "0.8rem", opacity: 0.7 }}>
            {L("In-app and Emergency notifications are always delivered. Urgent ignores quiet hours.",
               "تُسلَّم إشعارات التطبيق والطوارئ دائمًا. والعاجلة تتجاوز ساعات الهدوء.")}</p>
          <button className="btn" disabled={prefs.length === 0} onClick={savePrefs}>{L("Save preferences", "حفظ التفضيلات")}</button>
        </Card>
        <Card title={L("SLA policies", "سياسات مدة الإنجاز")}>
          <Table rows={slas} empty={L("No SLA policies (defaults apply).", "لا سياسات (تُطبق الافتراضيات).")} columns={[
            { label: L("Type", "النوع"), render: (s) => s.entityType },
            { label: L("Respond within (h)", "الاستجابة خلال (س)"), render: (s) => s.responseHours },
            { label: L("Escalate after (h)", "التصعيد بعد (س)"), render: (s) => s.escalateAfterHours },
          ]} />
          <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(120px, 1fr))", gap: "0.5rem", marginTop: "0.75rem" }}>
            <Field label={L("Type", "النوع")}><input className="input" value={entityType} onChange={(e) => setEntityType(e.target.value)} /></Field>
            <Field label={L("Hours", "ساعات")}><input className="input" type="number" value={hours} onChange={(e) => setHours(Number(e.target.value))} /></Field>
            <Field label={L("Escalate (h)", "تصعيد (س)")}><input className="input" type="number" value={escalate} onChange={(e) => setEscalate(Number(e.target.value))} /></Field>
          </div>
          <button className="btn btn-secondary" style={{ marginTop: "0.75rem" }} onClick={saveSla}>{L("Save SLA policy", "حفظ السياسة")}</button>
        </Card>
      </div>
    </Page>
  );
}
