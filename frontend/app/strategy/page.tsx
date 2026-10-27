"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { Card, Field, Kpi, Page, attempt, useL, useTenant } from "../../components/Ui";

type Plan = { id: string; title: string; yearFrom: number; yearTo: number };
type Reading = { period: string; value: number; recordedAt: string };
type KpiCard = {
  id: string; name: string; unit: string; baseline: number | null; target: number; current: number; lowerIsBetter: boolean;
  progress: number | null; status: "Achieved" | "OnTrack" | "AtRisk" | "OffTrack"; trend: "improving" | "worsening" | "flat";
  readings: Reading[]; accreditationEvidence: number;
};
type Scorecard = { plan: Plan; summary: Record<string, number>; objectives: { id: string; code: string; text: string; kpis: KpiCard[] }[] };

const COLOR: Record<string, string> = { Achieved: "#16a34a", OnTrack: "#0d9488", AtRisk: "#ca8a04", OffTrack: "#dc2626" };
const ARROW: Record<string, string> = { improving: "▲", worsening: "▼", flat: "▬" };

export default function StrategyPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [plans, setPlans] = useState<Plan[]>([]);
  const [planId, setPlanId] = useState("");
  const [card, setCard] = useState<Scorecard | null>(null);
  const [newPlan, setNewPlan] = useState({ title: "", yearFrom: new Date().getFullYear(), yearTo: new Date().getFullYear() + 4 });
  const [newObj, setNewObj] = useState({ code: "", text: "" });
  const [newKpi, setNewKpi] = useState({ objectiveId: "", name: "", unit: "%", baseline: "", target: "", current: "", lowerIsBetter: false });
  const [reading, setReading] = useState<Record<string, { value: string; period: string }>>({});

  const label: Record<string, string> = {
    Achieved: L("Achieved", "متحقق"), OnTrack: L("On track", "على المسار"), AtRisk: L("At risk", "معرض للخطر"), OffTrack: L("Off track", "خارج المسار"),
  };
  const post = (path: string, body: object) => api<{ id: string }>(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
  const open = async (id: string) => { setPlanId(id); setCard(id ? await api<Scorecard>(`/api/strategy/plans/${id}/scorecard?tenantId=${tenantId}`) : null); };
  const load = () => attempt(setErr, async () => {
    const list = await api<Plan[]>(`/api/strategy/plans?tenantId=${tenantId}`);
    setPlans(list);
    await open(planId || list[0]?.id || "");
  });
  const createPlan = () => attempt(setErr, async () => {
    const p = await post("/api/strategy/plans", newPlan);
    setNewPlan({ ...newPlan, title: "" });
    setPlans(await api<Plan[]>(`/api/strategy/plans?tenantId=${tenantId}`)); await open(p.id);
  });
  const createObjective = () => attempt(setErr, async () => {
    await post("/api/strategy/objectives", { planId, ...newObj }); setNewObj({ code: "", text: "" }); await open(planId);
  });
  const createKpi = () => attempt(setErr, async () => {
    await post("/api/strategy/kpis", {
      objectiveId: newKpi.objectiveId, name: newKpi.name, unit: newKpi.unit, target: Number(newKpi.target),
      current: newKpi.current === "" ? Number(newKpi.baseline || 0) : Number(newKpi.current),
      baseline: newKpi.baseline === "" ? null : Number(newKpi.baseline), lowerIsBetter: newKpi.lowerIsBetter,
    });
    setNewKpi({ ...newKpi, name: "", baseline: "", target: "", current: "" }); await open(planId);
  });
  const record = (k: KpiCard) => attempt(setErr, async () => {
    const r = reading[k.id];
    await post(`/api/strategy/kpis/${k.id}/reading`, { current: Number(r.value), period: r.period || null });
    setReading({ ...reading, [k.id]: { value: "", period: "" } }); await open(planId);
  });

  return (
    <Page title={L("Strategy Scorecard", "بطاقة الأداء الاستراتيجي")} code="UX-STR-001" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      <div className="card-grid">
        <Card title={L("Strategic plan", "الخطة الاستراتيجية")}>
          <Field label={L("Plan", "الخطة")}>
            <select className="input" value={planId} onChange={(e) => attempt(setErr, () => open(e.target.value))}>
              <option value="">–</option>
              {plans.map((p) => <option key={p.id} value={p.id}>{p.title} ({p.yearFrom}–{p.yearTo})</option>)}</select></Field>
          {card && (
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(90px, 1fr))", gap: "0.5rem", marginTop: "0.75rem" }}>
              {Object.entries(card.summary).map(([s, n]) => <Kpi key={s} label={label[s] ?? s} value={n} />)}
            </div>)}
          <div style={{ display: "grid", gap: "0.5rem", marginTop: "0.75rem" }}>
            <Field label={L("New plan title", "عنوان خطة جديدة")}>
              <input className="input" value={newPlan.title} onChange={(e) => setNewPlan({ ...newPlan, title: e.target.value })} /></Field>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "0.5rem" }}>
              <Field label={L("From year", "من سنة")}><input className="input" type="number" value={newPlan.yearFrom} onChange={(e) => setNewPlan({ ...newPlan, yearFrom: Number(e.target.value) })} /></Field>
              <Field label={L("To year", "إلى سنة")}><input className="input" type="number" value={newPlan.yearTo} onChange={(e) => setNewPlan({ ...newPlan, yearTo: Number(e.target.value) })} /></Field>
            </div>
            <button className="btn btn-secondary" disabled={!newPlan.title} onClick={createPlan}>{L("Create plan", "إنشاء خطة")}</button>
          </div>
        </Card>

        {card && card.objectives.map((o) => (
          <Card key={o.id} title={`${o.code} — ${o.text}`} wide>
            {o.kpis.length === 0 ? <p style={{ opacity: 0.7 }}>{L("No KPIs yet.", "لا توجد مؤشرات بعد.")}</p> : (
              <div style={{ display: "grid", gap: "0.75rem" }}>
                {o.kpis.map((k) => (
                  <div key={k.id} style={{ display: "grid", gridTemplateColumns: "minmax(180px, 2fr) minmax(160px, 2fr) minmax(220px, 3fr)", gap: "0.75rem", alignItems: "center" }}>
                    <div>
                      <strong>{k.name}</strong>
                      <div style={{ fontSize: "0.8rem", opacity: 0.75 }}>
                        {k.current} / {k.target} {k.unit}{k.baseline != null && ` · ${L("baseline", "خط الأساس")} ${k.baseline}`}
                        {k.lowerIsBetter && ` · ${L("lower is better", "الأقل أفضل")}`}
                        {k.accreditationEvidence > 0 && ` · ${L("cited in accreditation", "مستخدم في الاعتماد")}`}
                      </div>
                    </div>
                    <div>
                      <div role="progressbar" aria-label={k.name} aria-valuenow={k.progress ?? 0} aria-valuemin={0} aria-valuemax={100}
                        style={{ height: 8, borderRadius: 4, background: "rgba(148,163,184,0.3)" }}>
                        <div style={{ width: `${k.progress ?? 0}%`, height: "100%", borderRadius: 4, background: COLOR[k.status] }} />
                      </div>
                      <div style={{ fontSize: "0.8rem", marginTop: "0.25rem" }}>
                        <span className="badge" style={{ background: COLOR[k.status], color: "#fff" }}>{label[k.status]}</span>{" "}
                        <span title={k.readings.map((r) => `${r.period}: ${r.value}`).join(" · ")}>{ARROW[k.trend]} {k.progress ?? "–"}%</span>
                      </div>
                    </div>
                    <div style={{ display: "flex", gap: "0.25rem", flexWrap: "wrap" }}>
                      <input className="input" aria-label={`${L("New value for", "قيمة جديدة لـ")} ${k.name}`} type="number" style={{ width: "6rem" }}
                        value={reading[k.id]?.value ?? ""} onChange={(e) => setReading({ ...reading, [k.id]: { period: reading[k.id]?.period ?? "", value: e.target.value } })} />
                      <input className="input" aria-label={`${L("Period for", "الفترة لـ")} ${k.name}`} placeholder={L("Period", "الفترة")} style={{ width: "7rem" }}
                        value={reading[k.id]?.period ?? ""} onChange={(e) => setReading({ ...reading, [k.id]: { value: reading[k.id]?.value ?? "", period: e.target.value } })} />
                      <button className="btn btn-secondary" disabled={!reading[k.id]?.value} onClick={() => record(k)}>{L("Record", "تسجيل")}</button>
                    </div>
                  </div>))}
              </div>)}
          </Card>))}

        {card && (
          <Card title={L("Add objective or KPI", "إضافة هدف أو مؤشر")}>
            <div style={{ display: "grid", gridTemplateColumns: "1fr 2fr", gap: "0.5rem" }}>
              <Field label={L("Code", "الرمز")}><input className="input" value={newObj.code} onChange={(e) => setNewObj({ ...newObj, code: e.target.value })} /></Field>
              <Field label={L("Objective", "الهدف")}><input className="input" value={newObj.text} onChange={(e) => setNewObj({ ...newObj, text: e.target.value })} /></Field>
            </div>
            <button className="btn btn-secondary" style={{ marginTop: "0.5rem" }} disabled={!newObj.code || !newObj.text} onClick={createObjective}>{L("Add objective", "إضافة هدف")}</button>
            <hr style={{ margin: "1rem 0", opacity: 0.3 }} />
            <Field label={L("Objective", "الهدف")}>
              <select className="input" value={newKpi.objectiveId} onChange={(e) => setNewKpi({ ...newKpi, objectiveId: e.target.value })}>
                <option value="">–</option>
                {card.objectives.map((o) => <option key={o.id} value={o.id}>{o.code}</option>)}</select></Field>
            <Field label={L("KPI name", "اسم المؤشر")}><input className="input" value={newKpi.name} onChange={(e) => setNewKpi({ ...newKpi, name: e.target.value })} /></Field>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: "0.5rem" }}>
              <Field label={L("Baseline", "خط الأساس")}><input className="input" type="number" value={newKpi.baseline} onChange={(e) => setNewKpi({ ...newKpi, baseline: e.target.value })} /></Field>
              <Field label={L("Target", "المستهدف")}><input className="input" type="number" value={newKpi.target} onChange={(e) => setNewKpi({ ...newKpi, target: e.target.value })} /></Field>
              <Field label={L("Current", "الحالي")}><input className="input" type="number" value={newKpi.current} onChange={(e) => setNewKpi({ ...newKpi, current: e.target.value })} /></Field>
              <Field label={L("Unit", "الوحدة")}><input className="input" value={newKpi.unit} onChange={(e) => setNewKpi({ ...newKpi, unit: e.target.value })} /></Field>
            </div>
            <label style={{ display: "flex", gap: "0.5rem", alignItems: "center", margin: "0.5rem 0" }}>
              <input type="checkbox" checked={newKpi.lowerIsBetter} onChange={(e) => setNewKpi({ ...newKpi, lowerIsBetter: e.target.checked })} />
              {L("Lower is better (e.g. dropout rate)", "الأقل أفضل (مثل نسبة التسرب)")}</label>
            <button className="btn btn-secondary" disabled={!newKpi.objectiveId || !newKpi.name || newKpi.target === ""} onClick={createKpi}>{L("Add KPI", "إضافة مؤشر")}</button>
          </Card>)}
      </div>
    </Page>
  );
}
