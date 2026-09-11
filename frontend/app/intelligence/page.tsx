"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

export default function IntelligencePage() {
  const [tenantId, setTenantId] = useState("");
  const [err, setErr] = useState("");
  const [q, setQ] = useState("");
  const [results, setResults] = useState<{ correspondence: { title: string }[]; documents: { title: string }[]; people: { title: string }[]; decisions: { title: string }[]; policies: { title: string }[] } | null>(null);
  const [overview, setOverview] = useState<Record<string, unknown> | null>(null);
  const [question, setQuestion] = useState("");
  const [answer, setAnswer] = useState("");
  async function search() {
    setErr("");
    try { setResults(await api(`/api/search?tenantId=${tenantId}&q=${encodeURIComponent(q)}`)); }
    catch (e) { setErr(String(e)); }
  }
  async function loadOverview() {
    setErr("");
    try { setOverview(await api(`/api/analytics/overview?tenantId=${tenantId}`)); }
    catch (e) { setErr(String(e)); }
  }
  async function ask() {
    setErr("");
    try {
      await api(`/api/ai/index?tenantId=${tenantId}`, { method: "POST" });
      const r = await api<{ answer: string }>(`/api/ai/ask`, { method: "POST", body: JSON.stringify({ tenantId, question }) });
      setAnswer(r.answer);
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Institutional intelligence (UX-SRH-001 / UX-ANL-001 / UX-AI-001)</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <p style={{ color: "red" }}>{err}</p>
      <h3>Unified search</h3>
      <input placeholder="min 2 chars" value={q} onChange={(e) => setQ(e.target.value)} />{" "}
      <button onClick={search}>Search</button>
      {results && Object.entries(results).map(([k, v]) => <div key={k}><b>{k}</b><ul>{(v as { title: string }[]).map((x, i) => <li key={i}>{x.title}</li>)}</ul></div>)}
      <h3>Executive overview</h3>
      <button onClick={loadOverview}>Load</button>
      <pre>{overview && JSON.stringify(overview, null, 1)}</pre>
      <h3>AI copilot (Echo extractive)</h3>
      <input placeholder="ask about institutional content" value={question} onChange={(e) => setQuestion(e.target.value)} size={50} />{" "}
      <button onClick={ask}>Ask</button>
      <pre style={{ whiteSpace: "pre-wrap" }}>{answer}</pre>
    </main>
  );
}
