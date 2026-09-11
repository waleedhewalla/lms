"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

type Audit = { id: string; action: string; entityType: string; entityId: string; at: string; details?: string };

export default function AuditPage() {
  const [tenantId, setTenantId] = useState("");
  const [rows, setRows] = useState<Audit[]>([]);
  const [err, setErr] = useState("");
  async function load() {
    setErr("");
    try {
      setRows(await api<Audit[]>(`/api/audit?tenantId=${tenantId}&limit=100`));
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Audit trail</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <button onClick={load}>Load latest 100</button>
      <p style={{ color: "red" }}>{err}</p>
      <table border={1} cellPadding={4}>
        <thead><tr><th>At</th><th>Action</th><th>Entity</th><th>Details</th></tr></thead>
        <tbody>{rows.map((r) => <tr key={r.id}><td>{r.at}</td><td>{r.action}</td><td>{r.entityType}:{r.entityId.slice(0, 8)}</td><td>{r.details}</td></tr>)}</tbody>
      </table>
    </main>
  );
}
