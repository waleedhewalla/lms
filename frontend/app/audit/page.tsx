"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTranslation } from "../../components/TranslationProvider";

type Audit = { id: string; action: string; entityType: string; entityId: string; at: string; details?: string };

export default function AuditPage() {
  const { t } = useTranslation();
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
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '2rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("audit.title")}</h1>
        </div>
        <button className="btn btn-secondary" onClick={load}>{t("audit.load")}</button>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      
      {err && <div style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1.5rem', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{err}</div>}

      <div className="glass-card">
        <div style={{ overflowX: 'auto' }}>
          <table style={{ width: '100%', borderCollapse: 'collapse', textAlign: 'left' }}>
            <thead>
              <tr style={{ borderBottom: '1px solid var(--border)' }}>
                <th style={{ padding: '0.75rem', fontWeight: 600 }}>{t("audit.col.at")}</th>
                <th style={{ padding: '0.75rem', fontWeight: 600 }}>{t("audit.col.action")}</th>
                <th style={{ padding: '0.75rem', fontWeight: 600 }}>{t("audit.col.entity")}</th>
                <th style={{ padding: '0.75rem', fontWeight: 600 }}>{t("audit.col.details")}</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r) => (
                <tr key={r.id} style={{ borderBottom: '1px solid var(--border)' }}>
                  <td style={{ padding: '0.75rem', opacity: 0.8 }}>{new Date(r.at).toLocaleString()}</td>
                  <td style={{ padding: '0.75rem' }}><span className="badge">{r.action}</span></td>
                  <td style={{ padding: '0.75rem', fontFamily: 'monospace', fontSize: '0.9em' }}>{r.entityType}:{r.entityId.slice(0, 8)}</td>
                  <td style={{ padding: '0.75rem', opacity: 0.8 }}>{r.details || '-'}</td>
                </tr>
              ))}
              {rows.length === 0 && (
                <tr>
                  <td colSpan={4} style={{ padding: '2rem', textAlign: 'center', opacity: 0.5 }}>-</td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
