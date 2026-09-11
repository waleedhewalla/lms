"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";

type Role = { id: string; code: string; name: string };
type Assignment = { id: string; personId: string; roleId: string };

export default function RolesPage() {
  const [tenantId, setTenantId] = useState("");
  const [roles, setRoles] = useState<Role[]>([]);
  const [assignments, setAssignments] = useState<Assignment[]>([]);
  const [err, setErr] = useState("");
  const [code, setCode] = useState("");
  const [personId, setPersonId] = useState("");
  const [roleCode, setRoleCode] = useState("");
  async function load() {
    setErr("");
    try {
      setRoles(await api<Role[]>(`/api/roles?tenantId=${tenantId}`));
      setAssignments(await api<Assignment[]>(`/api/roles/assignments?tenantId=${tenantId}`));
    } catch (e) { setErr(String(e)); }
  }
  async function createRole() {
    setErr("");
    try {
      await api("/api/roles", { method: "POST", body: JSON.stringify({ tenantId, code, name: code, permissions: [] }) });
      setCode("");
      await load();
    } catch (e) { setErr(String(e)); }
  }
  async function assign() {
    setErr("");
    try {
      await api("/api/roles/assign", { method: "POST", body: JSON.stringify({ tenantId, personId, roleCode }) });
      await load();
    } catch (e) { setErr(String(e)); }
  }
  async function revoke() {
    setErr("");
    try {
      await api("/api/roles/revoke", { method: "POST", body: JSON.stringify({ tenantId, personId, roleCode }) });
      await load();
    } catch (e) { setErr(String(e)); }
  }
  return (
    <main style={{ padding: 24 }}>
      <h1>Roles & assignments (UX-ADM-001)</h1>
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      <button onClick={load}>Load</button>
      <p style={{ color: "red" }}>{err}</p>
      <h3>Roles</h3>
      <ul>{roles.map((r) => <li key={r.id}>{r.code} — {r.name}</li>)}</ul>
      <input placeholder="new role code" value={code} onChange={(e) => setCode(e.target.value)} />{" "}
      <button onClick={createRole}>Create role</button>
      <h3>Assign / revoke (SoD enforced server-side)</h3>
      <input placeholder="person id" value={personId} onChange={(e) => setPersonId(e.target.value)} size={40} />{" "}
      <input placeholder="role code" value={roleCode} onChange={(e) => setRoleCode(e.target.value)} />{" "}
      <button onClick={assign}>Assign</button> <button onClick={revoke}>Revoke</button>
      <h3>Assignments</h3>
      <ul>{assignments.map((a) => <li key={a.id}>{a.personId} → {a.roleId}</li>)}</ul>
    </main>
  );
}
