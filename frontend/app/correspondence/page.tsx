"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

type Corr = { id: string; number: string; subject: string; status: string; priority: string };

export default function CorrespondencePage() {
  const [tenantId, setTenantId] = useState("");
  const [items, setItems] = useState<Corr[]>([]);
  const [err, setErr] = useState("");
  const [subject, setSubject] = useState("");
  const [authorId, setAuthorId] = useState("");
  const [reviewerId, setReviewerId] = useState("");
  async function load() {
    setErr("");
    try { setItems(await api<Corr[]>(`/api/correspondence?tenantId=${tenantId}`)); }
    catch (e) { setErr(String(e)); }
  }
  async function create() {
    setErr("");
    try {
      await api("/api/correspondence", { method: "POST", body: JSON.stringify({ tenantId, type: "Internal", subject, content: subject, authorId, priority: "Normal", isConfidential: false }) });
      setSubject("");
      await load();
    } catch (e) { setErr(String(e)); }
  }
  async function submit(id: string) {
    setErr("");
    try {
      await api(`/api/correspondence/${id}/submit`, { method: "POST", body: JSON.stringify({ tenantId, reviewerId }) });
      await load();
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Correspondence (UX-COR-001)</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <button onClick={load}>Load</button>
      <p style={{ color: "red" }}>{err}</p>
      <ul>{items.map((c) => <li key={c.id}>{c.number} — {c.subject} [{c.status}/{c.priority}] {c.status === "Draft" && <button onClick={() => submit(c.id)}>Submit</button>}</li>)}</ul>
      <h3>New draft</h3>
      <input placeholder="subject" value={subject} onChange={(e) => setSubject(e.target.value)} />{" "}
      <input placeholder="author id" value={authorId} onChange={(e) => setAuthorId(e.target.value)} size={38} />{" "}
      <input placeholder="reviewer id (for submit)" value={reviewerId} onChange={(e) => setReviewerId(e.target.value)} size={38} />{" "}
      <button onClick={create}>Create</button>
    </main>
  );
}
