"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTranslation } from "../../components/TranslationProvider";

type Role = { id: string; code: string; name: string };
type Assignment = { id: string; personId: string; roleId: string };

export default function RolesPage() {
  const { t } = useTranslation();
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
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '2rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("roles.title")}</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.5rem' }}>UX-ADM-001</p>
        </div>
        <button className="btn btn-secondary" onClick={load}>{t("roles.refresh")}</button>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      
      {err && <div style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1.5rem', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{err}</div>}

      <div className="card-grid">
        <div className="glass-card">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1.5rem' }}>
            <h3 style={{ margin: 0 }}>{t("roles.avail.title")}</h3>
            <span className="badge">{roles.length} {t("roles.avail.total")}</span>
          </div>
          
          <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '1.5rem' }}>
            <input className="input" placeholder={t("roles.avail.placeholder")} value={code} onChange={(e) => setCode(e.target.value)} />
            <button className="btn" onClick={createRole}>{t("roles.avail.btn")}</button>
          </div>
          
          <div style={{ background: 'var(--background)', borderRadius: '0.5rem', padding: '1rem', flex: 1 }}>
            {roles.length === 0 ? (
              <p style={{ opacity: 0.5, textAlign: 'center', margin: 0 }}>{t("roles.avail.empty")}</p>
            ) : (
              <ul style={{ margin: 0, paddingLeft: '1.2rem', color: 'var(--foreground)' }}>
                {roles.map((r) => <li key={r.id} style={{ marginBottom: '0.5rem' }}><strong>{r.code}</strong> <span style={{ opacity: 0.7 }}>— {r.name}</span></li>)}
              </ul>
            )}
          </div>
        </div>

        <div className="glass-card">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1.5rem' }}>
            <h3 style={{ margin: 0 }}>{t("roles.assign.title")}</h3>
            <span className="badge">{assignments.length} {t("roles.avail.total")}</span>
          </div>
          
          <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', marginBottom: '1.5rem' }}>
            <input className="input" placeholder={t("roles.assign.person")} value={personId} onChange={(e) => setPersonId(e.target.value)} />
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <input className="input" placeholder={t("roles.assign.role")} value={roleCode} onChange={(e) => setRoleCode(e.target.value)} style={{ flex: 1 }} />
              <button className="btn" onClick={assign}>{t("roles.assign.btn")}</button>
              <button className="btn btn-secondary" onClick={revoke}>{t("roles.assign.revoke")}</button>
            </div>
          </div>
          
          <div style={{ background: 'var(--background)', borderRadius: '0.5rem', padding: '1rem', flex: 1 }}>
            {assignments.length === 0 ? (
              <p style={{ opacity: 0.5, textAlign: 'center', margin: 0 }}>{t("roles.assign.empty")}</p>
            ) : (
              <ul style={{ margin: 0, paddingLeft: '1.2rem', color: 'var(--foreground)' }}>
                {assignments.map((a) => <li key={a.id} style={{ marginBottom: '0.5rem' }}>{a.personId} <span style={{ color: 'var(--primary)', margin: '0 0.5rem' }}>→</span> <strong>{a.roleId}</strong></li>)}
              </ul>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
