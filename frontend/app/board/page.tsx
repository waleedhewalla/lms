"use client";
import { useState } from "react";
import { api, apiBase, authHeaders } from "../../lib/api";
import { Card, Field, Page, Table, attempt, fmtDate, useL, useTenant } from "../../components/Ui";

type Committee = { id: string; code: string; name: string; quorumPercent: number | null };
type Resolution = {
  id: string; committeeId: string; title: string; text: string; requiresUnanimous: boolean; status: string; closesAt: string;
  outcome: string | null; members: number; eligible: number; inFavor: number; against: number; abstain: number; recused: number; awaiting: number; needed: number;
};
type RegisterRow = { personId: string; name: string | null; role: string; declared: boolean; noInterests: boolean | null; interests: string | null; declaredAt: string | null };
type Register = { members: number; declared: number; rows: RegisterRow[] };

const COLOR: Record<string, string> = { Open: "#2563eb", Passed: "#16a34a", Failed: "#dc2626", Withdrawn: "#64748b", Expired: "#ca8a04" };

export default function BoardPage() {
  const L = useL();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [ok, setOk] = useState("");
  const [committees, setCommittees] = useState<Committee[]>([]);
  const [resolutions, setResolutions] = useState<Resolution[]>([]);
  const [draft, setDraft] = useState({ committeeId: "", title: "", text: "", closesAt: "", requiresUnanimous: false });
  const [remarks, setRemarks] = useState("");
  const [regCommittee, setRegCommittee] = useState("");
  const [period, setPeriod] = useState(String(new Date().getFullYear()));
  const [register, setRegister] = useState<Register | null>(null);
  const [mine, setMine] = useState({ noInterests: false, interests: "" });
  const [isSecretary, setIsSecretary] = useState(false);

  const status: Record<string, string> = {
    Open: L("Open", "مفتوح"), Passed: L("Passed", "معتمد"), Failed: L("Failed", "لم يُعتمد"), Withdrawn: L("Withdrawn", "مسحوب"), Expired: L("Expired", "منتهي"),
  };
  const post = (path: string, body: object) => api(path, { method: "POST", body: JSON.stringify({ tenantId, ...body }) });
  const loadResolutions = async () => setResolutions(await api<Resolution[]>(`/api/resolutions?tenantId=${tenantId}`));
  const load = () => attempt(setErr, async () => {
    setOk("");
    setIsSecretary((await api<{ permissions: string[] }>("/api/auth/me")).permissions.includes("meeting:create"));
    setCommittees(await api<Committee[]>(`/api/committees?tenantId=${tenantId}`));
    await loadResolutions();
  });
  const circulate = () => attempt(setErr, async () => {
    await post("/api/resolutions", { ...draft, closesAt: new Date(draft.closesAt).toISOString() });
    setDraft({ ...draft, title: "", text: "" });
    setOk(L("Circulated. Members were notified.", "تم التعميم وإشعار الأعضاء."));
    await loadResolutions();
  });
  const respond = (r: Resolution, choice: string) => attempt(setErr, async () => {
    await post(`/api/resolutions/${r.id}/vote`, { choice, remarks: remarks || null }); setRemarks(""); await loadResolutions();
  });
  const recuse = (r: Resolution) => attempt(setErr, async () => {
    await post(`/api/resolutions/${r.id}/recuse`, { reason: remarks }); setRemarks(""); await loadResolutions();
  });
  const withdraw = (r: Resolution) => attempt(setErr, async () => { await post(`/api/resolutions/${r.id}/withdraw`, {}); await loadResolutions(); });
  const certificate = (r: Resolution) => attempt(setErr, async () => {
    const res = await fetch(`${apiBase()}/api/resolutions/${r.id}/certificate?tenantId=${tenantId}`, { headers: authHeaders() });
    if (!res.ok) throw new Error(`certificate → ${res.status}`);
    const url = URL.createObjectURL(await res.blob());
    const a = document.createElement("a"); a.href = url; a.download = `resolution-${r.id.slice(0, 8)}.html`; a.click(); URL.revokeObjectURL(url);
  });
  const loadRegister = (cid = regCommittee) => attempt(setErr, async () => {
    if (!cid) return;
    setRegister(await api<Register>(`/api/governance/interests?tenantId=${tenantId}&committeeId=${cid}&period=${encodeURIComponent(period)}`));
  });
  const declare = () => attempt(setErr, async () => {
    await post("/api/governance/interests", { period, noInterests: mine.noInterests, interests: mine.noInterests ? null : mine.interests });
    setOk(L("Your declaration was recorded.", "تم تسجيل إقرارك."));
    await loadRegister();
  });
  const committeeName = (id: string) => committees.find((c) => c.id === id)?.code ?? id.slice(0, 8);

  return (
    <Page title={L("Board", "مجلس الإدارة")} code="UX-GOV-005" tenantId={tenantId} setTenantId={setTenantId} error={err}
      actions={<button className="btn" onClick={load}>{L("Load", "تحميل")}</button>}>
      {ok && <p role="status" style={{ color: "var(--primary)" }}>{ok}</p>}
      <div className="card-grid">
        <Card title={L("Written resolutions", "القرارات بالتمرير")} wide>
          <p style={{ marginTop: 0, fontSize: "0.85rem", opacity: 0.8 }}>
            {L("Decided in writing without a meeting. A resolution passes with a majority of all eligible members (or all of them when unanimous); members with a conflict recuse and are not counted.",
               "تُتخذ كتابيًا دون اجتماع. يُعتمد القرار بأغلبية جميع الأعضاء المؤهلين (أو بإجماعهم إذا اشتُرط)، ويتنحى صاحب المصلحة ولا يُحتسب.")}</p>
          <Table rows={resolutions} empty={L("No resolutions yet.", "لا توجد قرارات بعد.")} columns={[
            { label: L("Resolution", "القرار"), render: (r) => <span title={r.text}><strong>{r.title}</strong> · {committeeName(r.committeeId)}{r.requiresUnanimous && ` · ${L("unanimous", "بالإجماع")}`}</span> },
            { label: L("Status", "الحالة"), render: (r) => <span className="badge" style={{ background: COLOR[r.status], color: "#fff" }} title={r.outcome ?? undefined}>{status[r.status] ?? r.status}</span> },
            { label: L("In favour / needed", "موافق / المطلوب"), render: (r) => `${r.inFavor} / ${r.needed} (${L("of", "من")} ${r.eligible})` },
            { label: L("Against · abstain · recused", "معارض · ممتنع · متنحٍ"), render: (r) => `${r.against} · ${r.abstain} · ${r.recused}` },
            { label: L("Closes", "يُغلق"), render: (r) => fmtDate(r.closesAt) },
            { label: "", render: (r) => r.status === "Open" ? (
              <div style={{ display: "flex", gap: "0.25rem", flexWrap: "wrap" }}>
                <button className="btn" onClick={() => respond(r, "InFavor")}>{L("In favour", "موافق")}</button>
                <button className="btn btn-secondary" onClick={() => respond(r, "Against")}>{L("Against", "معارض")}</button>
                <button className="btn btn-secondary" onClick={() => respond(r, "Abstain")}>{L("Abstain", "ممتنع")}</button>
                <button className="btn btn-secondary" disabled={!remarks} title={L("Write the nature of the interest in the remarks box first", "اكتب طبيعة المصلحة في خانة الملاحظات أولاً")} onClick={() => recuse(r)}>{L("Recuse", "تنحٍّ")}</button>
                {isSecretary && <button className="btn btn-secondary" onClick={() => withdraw(r)}>{L("Withdraw", "سحب")}</button>}
              </div>) : (r.status !== "Withdrawn" && <button className="btn btn-secondary" onClick={() => certificate(r)}>{L("Certificate", "الشهادة")}</button>) },
          ]} />
          <Field label={L("Remarks, or the nature of your interest when recusing", "ملاحظات، أو طبيعة المصلحة عند التنحي")}>
            <input className="input" value={remarks} onChange={(e) => setRemarks(e.target.value)} /></Field>
        </Card>

        {isSecretary && <Card title={L("Circulate a resolution", "تعميم قرار")}>
          <Field label={L("Committee", "اللجنة")}>
            <select className="input" value={draft.committeeId} onChange={(e) => setDraft({ ...draft, committeeId: e.target.value })}>
              <option value="">–</option>
              {committees.map((c) => <option key={c.id} value={c.id}>{c.code} — {c.name}</option>)}</select></Field>
          <Field label={L("Title", "العنوان")}><input className="input" value={draft.title} onChange={(e) => setDraft({ ...draft, title: e.target.value })} /></Field>
          <Field label={L("Resolution text", "نص القرار")}><textarea className="input" rows={4} value={draft.text} onChange={(e) => setDraft({ ...draft, text: e.target.value })} /></Field>
          <Field label={L("Voting closes", "إغلاق التصويت")}><input className="input" type="datetime-local" value={draft.closesAt} onChange={(e) => setDraft({ ...draft, closesAt: e.target.value })} /></Field>
          <label style={{ display: "flex", gap: "0.5rem", alignItems: "center", margin: "0.5rem 0" }}>
            <input type="checkbox" checked={draft.requiresUnanimous} onChange={(e) => setDraft({ ...draft, requiresUnanimous: e.target.checked })} />
            {L("Requires unanimous consent", "يتطلب الإجماع")}</label>
          <button className="btn" disabled={!draft.committeeId || !draft.title || !draft.text || !draft.closesAt} onClick={circulate}>{L("Circulate for vote", "تعميم للتصويت")}</button>
        </Card>}

        <Card title={L("Interest register", "سجل الإفصاح عن المصالح")}>
          <div style={{ display: "grid", gridTemplateColumns: "2fr 1fr", gap: "0.5rem" }}>
            <Field label={L("Committee", "اللجنة")}>
              <select className="input" value={regCommittee} onChange={(e) => { setRegCommittee(e.target.value); loadRegister(e.target.value); }}>
                <option value="">–</option>
                {committees.map((c) => <option key={c.id} value={c.id}>{c.code}</option>)}</select></Field>
            <Field label={L("Period", "الفترة")}><input className="input" value={period} onChange={(e) => setPeriod(e.target.value)} /></Field>
          </div>
          {register && <p>{L(`${register.declared} of ${register.members} members have declared for ${period}.`, `أفصح ${register.declared} من ${register.members} عضوًا عن الفترة ${period}.`)}</p>}
          <Table rows={register?.rows ?? []} empty={L("Choose a committee.", "اختر لجنة.")} columns={[
            { label: L("Member", "العضو"), render: (r) => r.name ?? r.personId.slice(0, 8) },
            { label: L("Declaration", "الإفصاح"), render: (r) => !r.declared ? <span className="badge" style={{ background: "#dc2626", color: "#fff" }}>{L("Missing", "لم يُقدَّم")}</span>
              : r.noInterests ? L("No interests", "لا توجد مصالح") : r.interests },
          ]} />
          <hr style={{ margin: "1rem 0", opacity: 0.3 }} />
          <p style={{ marginTop: 0, fontWeight: 600 }}>{L("My declaration", "إفصاحي")}</p>
          <label style={{ display: "flex", gap: "0.5rem", alignItems: "center", marginBottom: "0.5rem" }}>
            <input type="checkbox" checked={mine.noInterests} onChange={(e) => setMine({ ...mine, noInterests: e.target.checked })} />
            {L("I have no interests to declare", "ليست لدي مصالح للإفصاح عنها")}</label>
          {!mine.noInterests && (
            <Field label={L("Interests (directorships, shareholdings, family relationships, gifts)", "المصالح (عضويات مجالس، حصص، صلات قرابة، هدايا)")}>
              <textarea className="input" rows={3} value={mine.interests} onChange={(e) => setMine({ ...mine, interests: e.target.value })} /></Field>)}
          <button className="btn btn-secondary" disabled={!mine.noInterests && !mine.interests} onClick={declare}>{L("Submit declaration", "تقديم الإفصاح")}</button>
        </Card>
      </div>
    </Page>
  );
}
