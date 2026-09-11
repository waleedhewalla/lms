"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

type Approval = { id: string; entityType: string; status: string; dueAt: string; priority: string };

export default function ApprovalsPage() {
  const [tenantId, setTenantId] = useState("");
  const [items, setItems] = useState<Approval[]>([]);
  const [err, setErr] = useState("");
  const [decidedBy, setDecidedBy] = useState("");
  const [breaches, setBreaches] = useState("");
  async function load() {
    setErr("");
    try {
      setItems(await api<Approval[]>(`/api/approvals?tenantId=${tenantId}&status=Pending`));
      const b = await api<{ approvals: unknown[]; tasks: unknown[] }>(`/api/sla/breaches?tenantId=${tenantId}`);
      setBreaches(`${b.approvals.length} approvals, ${b.tasks.length} tasks overdue`);
    } catch (e) { setErr(String(e)); }
  }
  async function decide(id: string, approve: boolean) {
    setErr("");
    try {
      await api(`/api/approvals/${id}/decide`, { method: "POST", body: JSON.stringify({ tenantId, decidedBy, approve }) });
      await load();
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Approval workspace (UX-APR-006)</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <input placeholder="decider person id" value={decidedBy} onChange={(e) => setDecidedBy(e.target.value)} size={38} />{" "}
      <button onClick={load}>Load pending</button>
      <p>SLA breaches: {breaches}</p>
      <p style={{ color: "red" }}>{err}</p>
      <ul>{items.map((a) => <li key={a.id}>{a.entityType} [{a.priority}] due {a.dueAt} <button onClick={() => decide(a.id, true)}>Approve</button> <button onClick={() => decide(a.id, false)}>Reject</button></li>)}</ul>
    </main>
  );
}
