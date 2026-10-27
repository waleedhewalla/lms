"use client";
import { useEffect, useState } from "react";
import { api, apiBase, authHeaders } from "../lib/api";
import { Card, Field, Table, attempt, fmtDate, useL } from "./Ui";

type Todo = { policyId: string; code: string; title: string; version: number; current: boolean; reason: "never" | "new-version" | "cycle" | null; lastAttestedAt: string | null; dueAgain: string | null; quizQuestions: number };
type Status = { policyId: string; code: string; title: string; version: number; attestEveryMonths: number | null; passPercent: number; quizQuestions: number; activePeople: number; current: number; outstanding: number; percent: number };
type Quiz = { version: number; attestEveryMonths: number | null; passPercent: number; questions: { order: number; question: string; options: string[]; correctIndex: number | null }[] };
type EditQ = { question: string; options: string; correctIndex: number };

/** The signed-in person's attestations, and compliance for policy owners. */
export function MyAttestations({ tenantId, setErr, onOpen, refreshKey }: { tenantId: string; setErr: (e: string) => void; onOpen: (policyId: string) => void; refreshKey: number }) {
  const L = useL();
  const [todo, setTodo] = useState<Todo[] | null>(null);
  const [status, setStatus] = useState<Status[]>([]);
  const [msg, setMsg] = useState("");
  const reason = { never: L("Not yet acknowledged", "لم يُقرّ بعد"), "new-version": L("New version", "نسخة جديدة"), cycle: L("Annual re-attestation due", "حان موعد إعادة الإقرار") };
  useEffect(() => {
    if (!tenantId || refreshKey === 0) return;
    attempt(setErr, async () => {
      setTodo(await api<Todo[]>(`/api/policies/my-attestations?tenantId=${tenantId}`).catch(() => []));
      setStatus(await api<Status[]>(`/api/policies/attestation-status?tenantId=${tenantId}`).catch(() => []));
    });
  }, [tenantId, refreshKey, setErr]);
  const remind = (s: Status) => attempt(setErr, async () => {
    const r = await api<{ reminded: number }>(`/api/policies/${s.policyId}/remind`, { method: "POST", body: JSON.stringify({ tenantId }) });
    setMsg(L(`${r.reminded} people reminded about ${s.code}.`, `تم تذكير ${r.reminded} شخصًا بشأن ${s.code}.`));
  });
  return (<>
    <Card title={L("My attestations", "إقراراتي")}>
      <Table rows={todo ?? []} empty={todo ? L("Nothing to attest.", "لا توجد إقرارات مطلوبة.") : L("Load to see your attestations.", "حمّل لعرض إقراراتك.")} columns={[
        { label: L("Policy", "السياسة"), render: (t) => `${t.code} — ${t.title} (v${t.version})` },
        { label: L("Status", "الحالة"), render: (t) => t.current
          ? <span className="badge" style={{ background: "#16a34a", color: "#fff" }} title={t.dueAgain ? `${L("Due again", "يُستحق مجددًا")} ${fmtDate(t.dueAgain)}` : undefined}>{L("Done", "تم")}</span>
          : <span className="badge" style={{ background: "#dc2626", color: "#fff" }}>{reason[t.reason ?? "never"]}</span> },
        { label: "", render: (t) => !t.current && <button className="btn" onClick={() => onOpen(t.policyId)}>{t.quizQuestions ? L("Read & take quiz", "اقرأ وأجب عن الاختبار") : L("Read & acknowledge", "اقرأ وأقرّ")}</button> },
      ]} />
    </Card>
    {status.length > 0 && (
      <Card title={L("Attestation compliance", "الالتزام بالإقرارات")}>
        {msg && <p role="status" style={{ color: "var(--primary)", marginTop: 0 }}>{msg}</p>}
        <Table rows={status} empty="" columns={[
          { label: L("Policy", "السياسة"), render: (s) => `${s.code} v${s.version}` },
          { label: L("Current", "ساري"), render: (s) => `${s.current}/${s.activePeople} (${s.percent}%)` },
          { label: L("Rule", "القاعدة"), render: (s) => `${s.attestEveryMonths ? L(`every ${s.attestEveryMonths} mo`, `كل ${s.attestEveryMonths} شهرًا`) : L("per version", "لكل نسخة")}${s.quizQuestions ? ` · ${L("quiz", "اختبار")} ≥${s.passPercent}%` : ""}` },
          { label: "", render: (s) => s.outstanding > 0 && <button className="btn btn-secondary" onClick={() => remind(s)}>{L("Remind", "تذكير")} ({s.outstanding})</button> },
        ]} />
      </Card>)}
  </>);
}

/** Read the policy, answer the quiz (if any) and attest. */
export function AttestPanel({ tenantId, policyId, content, setErr, onDone }: { tenantId: string; policyId: string; content: string; setErr: (e: string) => void; onDone: () => void }) {
  const L = useL();
  const [quiz, setQuiz] = useState<Quiz | null>(null);
  const [answers, setAnswers] = useState<number[]>([]);
  const [result, setResult] = useState("");
  useEffect(() => { attempt(setErr, async () => { const q = await api<Quiz>(`/api/policies/${policyId}/quiz?tenantId=${tenantId}`); setQuiz(q); setAnswers(q.questions.map(() => -1)); }); }, [policyId, tenantId, setErr]);
  const submit = () => attempt(setErr, async () => {
    setResult("");
    const res = await fetch(`${apiBase()}/api/policies/${policyId}/attest`, {
      method: "POST", headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify({ tenantId, answers }),
    });
    if (res.status === 422) { const b = await res.json(); setResult(L(`Score ${b.score}% — the pass mark is ${b.passPercent}%. Re-read the policy and try again.`, `النتيجة ${b.score}% — درجة النجاح ${b.passPercent}%. أعد قراءة السياسة وحاول مجددًا.`)); return; }
    if (res.status === 409) { setResult(L("You have already acknowledged this version.", "سبق أن أقررت بهذه النسخة.")); onDone(); return; }
    if (!res.ok) throw new Error(`attest → ${res.status}`);
    setResult(L("Thank you — your acknowledgement is recorded.", "شكرًا — تم تسجيل إقرارك."));
    onDone();
  });
  if (!quiz) return null;
  return (
    <div style={{ border: "1px solid var(--border)", borderRadius: "0.5rem", padding: "1rem", marginBottom: "1rem" }}>
      <h4 style={{ marginTop: 0 }}>{L("Read and acknowledge", "اقرأ وأقرّ")} (v{quiz.version})</h4>
      <div style={{ whiteSpace: "pre-wrap", maxHeight: 240, overflow: "auto", padding: "0.75rem", background: "rgba(148,163,184,0.1)", borderRadius: "0.375rem" }}>{content}</div>
      {quiz.questions.map((q, i) => (
        <fieldset key={q.order} style={{ border: "none", padding: 0, margin: "0.75rem 0" }}>
          <legend style={{ fontWeight: 600 }}>{i + 1}. {q.question}</legend>
          {q.options.map((o, j) => (
            <label key={j} style={{ display: "flex", gap: "0.5rem", alignItems: "center" }}>
              <input type="radio" name={`q${i}`} checked={answers[i] === j} onChange={() => setAnswers(answers.map((a, k) => (k === i ? j : a)))} />{o}
            </label>))}
        </fieldset>))}
      {quiz.questions.length > 0 && <p style={{ fontSize: "0.8rem", opacity: 0.7 }}>{L(`Pass mark ${quiz.passPercent}%.`, `درجة النجاح ${quiz.passPercent}%.`)}</p>}
      <button className="btn" disabled={answers.some((a) => a < 0)} onClick={submit}>{L("I have read and understood this policy", "قرأت هذه السياسة وفهمتها")}</button>
      {result && <p role="status" style={{ marginBottom: 0 }}>{result}</p>}
    </div>
  );
}

/** Policy owners: re-attestation cycle, pass mark and quiz questions. */
export function AttestationSettings({ tenantId, policyId, setErr }: { tenantId: string; policyId: string; setErr: (e: string) => void }) {
  const L = useL();
  const [months, setMonths] = useState("");
  const [pass, setPass] = useState("80");
  const [qs, setQs] = useState<EditQ[]>([]);
  const [saved, setSaved] = useState("");
  const [editor, setEditor] = useState(false);
  useEffect(() => {
    attempt(setErr, async () => {
      // Only policy owners see this editor (and the correct answers).
      const me = await api<{ permissions: string[] }>("/api/auth/me").catch(() => ({ permissions: [] as string[] }));
      if (!me.permissions.includes("policy:create")) return;
      setEditor(true);
      const q = await api<Quiz>(`/api/policies/${policyId}/quiz?tenantId=${tenantId}`);
      setMonths(q.attestEveryMonths ? String(q.attestEveryMonths) : ""); setPass(String(q.passPercent));
      setQs(q.questions.map((x) => ({ question: x.question, options: x.options.join("\n"), correctIndex: x.correctIndex ?? 0 })));
    });
  }, [policyId, tenantId, setErr]);
  const save = () => attempt(setErr, async () => {
    await api(`/api/policies/${policyId}/attestation`, {
      method: "PUT",
      body: JSON.stringify({
        tenantId, attestEveryMonths: months ? Number(months) : null, quizPassPercent: Number(pass),
        questions: qs.map((q) => ({ question: q.question, options: q.options.split("\n").map((o) => o.trim()).filter(Boolean), correctIndex: q.correctIndex })),
      }),
    });
    setSaved(L("Attestation settings saved.", "تم حفظ إعدادات الإقرار."));
  });
  const set = (i: number, patch: Partial<EditQ>) => setQs(qs.map((q, j) => (j === i ? { ...q, ...patch } : q)));
  if (!editor) return null;
  return (
    <div>
      <h4 style={{ marginTop: 0 }}>{L("Attestation", "الإقرار")}</h4>
      <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "0.5rem" }}>
        <Field label={L("Re-attest every (months)", "إعادة الإقرار كل (أشهر)")}><input className="input" type="number" min={1} max={60} placeholder={L("per version", "لكل نسخة")} value={months} onChange={(e) => setMonths(e.target.value)} /></Field>
        <Field label={L("Quiz pass mark (%)", "درجة النجاح (%)")}><input className="input" type="number" min={1} max={100} value={pass} onChange={(e) => setPass(e.target.value)} /></Field>
      </div>
      {qs.map((q, i) => (
        <div key={i} style={{ border: "1px solid var(--border)", borderRadius: "0.375rem", padding: "0.5rem", marginTop: "0.5rem" }}>
          <Field label={`${L("Question", "سؤال")} ${i + 1}`}><input className="input" value={q.question} onChange={(e) => set(i, { question: e.target.value })} /></Field>
          <Field label={L("Options (one per line)", "الخيارات (خيار في كل سطر)")}><textarea className="input" rows={3} value={q.options} onChange={(e) => set(i, { options: e.target.value })} /></Field>
          <div style={{ display: "flex", gap: "0.5rem", alignItems: "end" }}>
            <Field label={L("Correct option", "الخيار الصحيح")}>
              <select className="input" value={q.correctIndex} onChange={(e) => set(i, { correctIndex: Number(e.target.value) })}>
                {q.options.split("\n").filter((o) => o.trim()).map((o, j) => <option key={j} value={j}>{o}</option>)}</select></Field>
            <button className="btn btn-secondary" onClick={() => setQs(qs.filter((_, j) => j !== i))}>{L("Remove", "حذف")}</button>
          </div>
        </div>))}
      <div style={{ display: "flex", gap: "0.5rem", marginTop: "0.5rem" }}>
        <button className="btn btn-secondary" disabled={qs.length >= 20} onClick={() => setQs([...qs, { question: "", options: "", correctIndex: 0 }])}>{L("Add question", "إضافة سؤال")}</button>
        <button className="btn" onClick={save}>{L("Save attestation settings", "حفظ إعدادات الإقرار")}</button>
      </div>
      {saved && <p role="status" style={{ color: "var(--primary)" }}>{saved}</p>}
    </div>
  );
}
