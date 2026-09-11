"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

type Person = { id: string; fullName: string; email?: string; type: string };

export default function DirectoryPage() {
  const [tenantId, setTenantId] = useState("");
  const [q, setQ] = useState("");
  const [people, setPeople] = useState<Person[]>([]);
  const [err, setErr] = useState("");
  const [name, setName] = useState("");
  async function search() {
    setErr("");
    try {
      setPeople(await api<Person[]>(`/api/people?tenantId=${tenantId}&q=${encodeURIComponent(q)}`));
    } catch (e) { setErr(String(e)); }
  }
  async function create() {
    setErr("");
    try {
      await api("/api/people", { method: "POST", body: JSON.stringify({ tenantId, type: "Employee", fullName: name }) });
      setName("");
      await search();
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Directory (UX-DIR-001)</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <input placeholder="search…" value={q} onChange={(e) => setQ(e.target.value)} />{" "}
      <button onClick={search}>Search</button>
      <p style={{ color: "red" }}>{err}</p>
      <ul>{people.map((p) => <li key={p.id}>{p.fullName} — {p.email ?? "no email"} ({p.type})</li>)}</ul>
      <h3>Add employee</h3>
      <input placeholder="Full name" value={name} onChange={(e) => setName(e.target.value)} />{" "}
      <button onClick={create}>Create</button>
    </main>
  );
}
