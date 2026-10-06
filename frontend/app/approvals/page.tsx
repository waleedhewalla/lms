"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTranslation } from "../../components/TranslationProvider";
import { Chatter } from "../../components/Chatter";

type Approval = { id: string; entityType: string; status: string; dueAt: string; priority: string };

export default function ApprovalsPage() {
  const { t } = useTranslation();
  const [tenantId, setTenantId] = useState("");
  const [items, setItems] = useState<Approval[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
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
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '2rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("page.apr.title")}</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.5rem' }}>UX-APR-006</p>
        </div>
        <button className="btn btn-secondary" onClick={load}>{t("page.apr.load")}</button>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      
      {err && <div style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1.5rem', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{err}</div>}

      <div className="card-grid">
        <div className="glass-card">
          <ul style={{ margin: 0, paddingLeft: '1.2rem', color: 'var(--foreground)' }}>
            {items.map((a) => (
              <li
                key={a.id}
                onClick={() => setSelectedId(a.id)}
                style={{
                  marginBottom: '0.5rem',
                  padding: '0.5rem',
                  borderRadius: '0.375rem',
                  cursor: 'pointer',
                  background: selectedId === a.id ? 'rgba(59, 130, 246, 0.1)' : 'transparent'
                }}
              >
                <strong>{a.entityType}</strong> <span className="badge">[{a.priority}]</span> due {new Date(a.dueAt).toLocaleString()}
                <button className="btn" style={{ padding: '0.2rem 0.5rem', fontSize: '0.8rem', marginLeft: '0.5rem' }} onClick={(e) => { e.stopPropagation(); decide(a.id, true); }}>{t("page.apr.approve")}</button>
                <button className="btn btn-secondary" style={{ padding: '0.2rem 0.5rem', fontSize: '0.8rem', marginLeft: '0.5rem' }} onClick={(e) => { e.stopPropagation(); decide(a.id, false); }}>{t("page.apr.reject")}</button>
              </li>
            ))}
          </ul>
        </div>

        <div className="glass-card">
          <h3 style={{ margin: '0 0 1.5rem 0' }}>{t("page.apr.sla")}</h3>
          <p style={{ fontWeight: 600 }}>{breaches || "-"}</p>
          
          <div style={{ display: 'flex', flexDirection: 'column', gap: '1rem', marginTop: '1.5rem' }}>
            <input className="input" placeholder={t("page.apr.decider")} value={decidedBy} onChange={(e) => setDecidedBy(e.target.value)} />
          </div>

          {selectedId && (
            <div style={{ marginTop: '1.5rem', paddingTop: '1.5rem', borderTop: '1px solid var(--border)' }}>
              <Chatter
                entityType="Approval"
                entityId={selectedId}
                tenantId={tenantId || "11111111-1111-1111-1111-111111111111"}
                currentPersonId="00000000-0000-0000-0000-000000000001"
              />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
