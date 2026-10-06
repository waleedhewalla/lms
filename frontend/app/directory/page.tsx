"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTenant } from "../../components/Ui";
import { useTranslation } from "../../components/TranslationProvider";

type Person = { id: string; fullName: string; email?: string; type: string };
type Delegation = { id: string; fromPersonId: string; toPersonId: string; scope: string; expiresAt: string };

export default function DirectoryPage() {
  const { t } = useTranslation();
  const [tenantId, setTenantId] = useTenant();
  const [q, setQ] = useState("");
  const [people, setPeople] = useState<Person[]>([]);
  const [err, setErr] = useState("");
  const [name, setName] = useState("");

  // Authority Delegation state (Odoo HR pattern)
  const [selectedPerson, setSelectedPerson] = useState<Person | null>(null);
  const [delegations, setDelegations] = useState<Delegation[]>([]);
  const [deputyId, setDeputyId] = useState("");
  const [delegationScope, setDelegationScope] = useState("Workflow;Approval");
  const [delegationDays, setDelegationDays] = useState(14);

  async function search() {
    setErr("");
    try {
      const res = await api<Person[]>(`/api/people?tenantId=${tenantId}&q=${encodeURIComponent(q)}`);
      setPeople(res);
      if (res.length > 0 && !selectedPerson) {
        selectPerson(res[0]);
      }
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

  async function selectPerson(p: Person) {
    setSelectedPerson(p);
    setErr("");
    try {
      const dels = await api<Delegation[]>(`/api/people/${p.id}/delegations?tenantId=${tenantId}`);
      setDelegations(dels);
    } catch (e) { setErr(String(e)); }
  }

  async function createDelegation() {
    if (!selectedPerson || !deputyId) return;
    setErr("");
    try {
      const expiresAt = new Date(Date.now() + delegationDays * 86400000).toISOString();
      await api(`/api/people/${selectedPerson.id}/delegations`, {
        method: "POST",
        body: JSON.stringify({
          tenantId,
          fromPersonId: selectedPerson.id,
          toPersonId: deputyId,
          scope: delegationScope,
          expiresAt
        })
      });
      await selectPerson(selectedPerson);
    } catch (e) { setErr(String(e)); }
  }

  async function revokeDelegation(delId: string) {
    if (!selectedPerson) return;
    setErr("");
    try {
      await api(`/api/people/${selectedPerson.id}/delegations/${delId}?tenantId=${tenantId}`, { method: "DELETE" });
      await selectPerson(selectedPerson);
    } catch (e) { setErr(String(e)); }
  }

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '2rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("dir.title")}</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.5rem' }}>UX-DIR-001 / ODOO-HR-001</p>
        </div>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      
      {err && <div style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1.5rem', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{err}</div>}

      <div className="card-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))' }}>
        <div className="glass-card">
          <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '1.5rem' }}>
            <input className="input" placeholder={t("dir.search.placeholder")} value={q} onChange={(e) => setQ(e.target.value)} />
            <button className="btn" onClick={search}>{t("dir.search.btn")}</button>
          </div>
          
          <div style={{ background: 'var(--background)', borderRadius: '0.5rem', padding: '1rem', maxHeight: '350px', overflowY: 'auto' }}>
            {people.length === 0 ? (
              <p style={{ opacity: 0.5, textAlign: 'center', margin: 0 }}>{t("dir.empty")}</p>
            ) : (
              <ul style={{ margin: 0, padding: 0, listStyle: 'none' }}>
                {people.map((p) => {
                  const isSel = selectedPerson?.id === p.id;
                  return (
                    <li
                      key={p.id}
                      onClick={() => selectPerson(p)}
                      style={{
                        padding: '0.75rem',
                        borderRadius: '0.375rem',
                        marginBottom: '0.5rem',
                        cursor: 'pointer',
                        background: isSel ? 'rgba(59, 130, 246, 0.15)' : 'transparent',
                        border: isSel ? '1px solid #3b82f6' : '1px solid transparent'
                      }}
                    >
                      <div style={{ fontWeight: 600 }}>{p.fullName}</div>
                      <div style={{ opacity: 0.7, fontSize: '0.8rem' }}>{p.email ?? "no email"} • {p.type}</div>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>

          <div style={{ marginTop: '1.5rem', paddingTop: '1.5rem', borderTop: '1px solid var(--border)' }}>
            <h4 style={{ margin: '0 0 1rem 0' }}>{t("dir.add.title")}</h4>
            <div style={{ display: 'flex', gap: '0.5rem' }}>
              <input className="input" placeholder={t("dir.add.placeholder")} value={name} onChange={(e) => setName(e.target.value)} />
              <button className="btn" onClick={create}>{t("dir.add.btn")}</button>
            </div>
          </div>
        </div>

        {/* Standing Delegations Panel (Odoo HR Pattern) */}
        <div className="glass-card">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1rem' }}>
            <h3 style={{ margin: 0 }}>{t("dir.delegations.title")}</h3>
            <span className="badge" style={{ background: 'rgba(99, 102, 241, 0.2)', color: '#818cf8' }}>{t("dir.delegations.pattern")}</span>
          </div>

          {selectedPerson ? (
            <div>
              <p style={{ fontSize: '0.9rem', opacity: 0.8, margin: '0 0 1rem 0' }}>
                {t("dir.delegations.desc")} <b>{selectedPerson.fullName}</b> {t("dir.delegations.desc_suffix")}
              </p>

              {/* Active Delegations List */}
              <div style={{ marginBottom: '1.5rem' }}>
                <h4 style={{ margin: '0 0 0.5rem 0', fontSize: '0.95rem' }}>{t("dir.delegations.active")}</h4>
                {delegations.filter(d => new Date(d.expiresAt) >= new Date()).length === 0 ? (
                  <div style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', opacity: 0.6, fontSize: '0.85rem' }}>
                    {t("dir.delegations.empty")}
                  </div>
                ) : (
                  <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                    {delegations.filter(d => new Date(d.expiresAt) >= new Date()).map((d) => {
                      const deputy = people.find((p) => p.id === d.toPersonId);
                      return (
                        <div key={d.id} style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', border: '1px solid var(--border)', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                          <div>
                            <div style={{ fontWeight: 600, fontSize: '0.9rem' }}>
                              {t("dir.delegations.deputy")}: {deputy ? deputy.fullName : d.toPersonId}
                            </div>
                            <div style={{ fontSize: '0.75rem', opacity: 0.7, marginTop: '0.2rem' }}>
                              {t("dir.delegations.scope")}: <span className="badge">[{d.scope}]</span> • {t("dir.delegations.expires")}: {new Date(d.expiresAt).toLocaleDateString()}
                            </div>
                          </div>
                          <button className="btn btn-secondary" style={{ padding: '0.3rem 0.6rem', fontSize: '0.75rem', color: '#ef4444' }} onClick={() => revokeDelegation(d.id)}>
                            {t("dir.delegations.revoke")}
                          </button>
                        </div>
                      );
                    })}
                  </div>
                )}

                {/* Expired Delegations Disclosure */}
                {delegations.some(d => new Date(d.expiresAt) < new Date()) && (
                  <details style={{ marginTop: '1rem' }}>
                    <summary style={{ cursor: 'pointer', opacity: 0.7, fontSize: '0.85rem', fontWeight: 600 }}>
                      {t("dir.delegations.show_expired")} ({delegations.filter(d => new Date(d.expiresAt) < new Date()).length})
                    </summary>
                    <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', marginTop: '0.5rem' }}>
                      {delegations.filter(d => new Date(d.expiresAt) < new Date()).map((d) => {
                        const deputy = people.find((p) => p.id === d.toPersonId);
                        return (
                          <div key={d.id} style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', border: '1px solid var(--border)', opacity: 0.6, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                            <div>
                              <div style={{ fontWeight: 600, fontSize: '0.9rem' }}>
                                {t("dir.delegations.deputy")}: {deputy ? deputy.fullName : d.toPersonId}
                              </div>
                              <div style={{ fontSize: '0.75rem', opacity: 0.7, marginTop: '0.2rem' }}>
                                {t("dir.delegations.scope")}: <span className="badge">[{d.scope}]</span> • {t("dir.delegations.expires")}: {new Date(d.expiresAt).toLocaleDateString()} <span style={{ color: '#ef4444', marginLeft: '0.5rem' }}>({t("dir.delegations.expired")})</span>
                              </div>
                            </div>
                            <button className="btn btn-secondary" style={{ padding: '0.3rem 0.6rem', fontSize: '0.75rem', color: '#ef4444' }} onClick={() => revokeDelegation(d.id)}>
                              {t("dir.delegations.revoke")}
                            </button>
                          </div>
                        );
                      })}
                    </div>
                  </details>
                )}
              </div>

              {/* Appoint Deputy Form */}
              <div style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                <h4 style={{ margin: '0 0 0.75rem 0', fontSize: '0.95rem' }}>{t("dir.delegations.appoint.title")}</h4>
                <div style={{ display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
                  <select className="input" value={deputyId} onChange={(e) => setDeputyId(e.target.value)}>
                    <option value="">{t("dir.delegations.appoint.select")}</option>
                    {people.filter((p) => p.id !== selectedPerson.id).map((p) => (
                      <option key={p.id} value={p.id}>{p.fullName} ({p.type})</option>
                    ))}
                  </select>

                  <div style={{ display: 'grid', gridTemplateColumns: '2fr 1fr', gap: '0.5rem' }}>
                    <input className="input" placeholder={t("dir.delegations.appoint.scope_ph")} value={delegationScope} onChange={(e) => setDelegationScope(e.target.value)} />
                    <input className="input" type="number" min="1" max="365" placeholder={t("dir.delegations.appoint.days_ph")} value={delegationDays} onChange={(e) => setDelegationDays(parseInt(e.target.value) || 14)} />
                  </div>

                  <button className="btn" onClick={createDelegation} disabled={!deputyId}>
                    {t("dir.delegations.appoint.btn")}
                  </button>
                </div>
              </div>
            </div>
          ) : (
            <div style={{ textAlign: 'center', padding: '2rem', opacity: 0.6 }}>
              {t("dir.delegations.none_selected")}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
