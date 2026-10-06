"use client";
import { useState } from "react";
import { api } from "../../lib/api";
import { ConnectionBar } from "../../components/ConnectionBar";
import { useTenant } from "../../components/Ui";
import { useTranslation } from "../../components/TranslationProvider";

type DocumentActionRule = {
  id: string;
  triggerCategory: string;
  triggerValue: string;
  actionType: string;
  targetValue?: string;
  isActive: boolean;
};

type RetentionReviewItem = {
  policy: {
    id: string;
    documentId: string;
    standard: string;
    retentionPeriodMonths: number;
    dispositionAction: string;
    nextReviewDueAt: string;
    notes?: string;
  };
  document?: {
    id: string;
    title: string;
    classification?: string;
    retainUntil?: string;
  };
};

export default function IntelligencePage() {
  const { t } = useTranslation();
  const [tenantId, setTenantId] = useTenant();
  const [err, setErr] = useState("");
  const [q, setQ] = useState("");
  const [results, setResults] = useState<{ correspondence: { title: string }[]; documents: { title: string }[]; people: { title: string }[]; decisions: { title: string }[]; policies: { title: string }[] } | null>(null);
  const [overview, setOverview] = useState<Record<string, unknown> | null>(null);
  const [question, setQuestion] = useState("");
  const [answer, setAnswer] = useState("");

  // Odoo Documents Action Rules & Folderit Retention state
  const [actionRules, setActionRules] = useState<DocumentActionRule[]>([]);
  const [reviews, setReviews] = useState<RetentionReviewItem[]>([]);
  const [ruleCategory, setRuleCategory] = useState("Classification");
  const [ruleValue, setRuleValue] = useState("Accreditation");
  const [ruleActionType, setRuleActionType] = useState("AutoRetention");
  const [ruleTargetValue, setRuleTargetValue] = useState(
    '{"standard":"ISO-9001:2015","retentionMonths":120,"disposition":"PermanentPreservation"}'
  );

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

  async function loadDocumentControl() {
    if (!tenantId) return;
    setErr("");
    try {
      const [r, rev] = await Promise.all([
        api<DocumentActionRule[]>(`/api/documents/action-rules?tenantId=${tenantId}`),
        api<RetentionReviewItem[]>(`/api/documents/retention-reviews?tenantId=${tenantId}`)
      ]);
      setActionRules(r);
      setReviews(rev);
    } catch (e) {
      setErr(String(e));
    }
  }

  async function createActionRule() {
    if (!tenantId) return;
    setErr("");
    try {
      await api("/api/documents/action-rules", {
        method: "POST",
        body: JSON.stringify({
          tenantId,
          triggerCategory: ruleCategory,
          triggerValue: ruleValue,
          actionType: ruleActionType,
          targetValue: ruleTargetValue
        })
      });
      await loadDocumentControl();
    } catch (e) {
      setErr(String(e));
    }
  }

  async function deleteActionRule(ruleId: string) {
    if (!tenantId) return;
    setErr("");
    try {
      await api(`/api/documents/action-rules/${ruleId}?tenantId=${tenantId}`, { method: "DELETE" });
      await loadDocumentControl();
    } catch (e) {
      setErr(String(e));
    }
  }

  return (
    <div>
      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '2rem' }}>
        <div>
          <h1 style={{ margin: 0, fontSize: '2rem' }}>{t("page.intel.title")}</h1>
          <p style={{ color: 'var(--primary)', fontWeight: 600, marginTop: '0.5rem' }}>UX-SRH-001 / UX-ANL-001 / UX-AI-001</p>
        </div>
      </div>

      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      
      {err && <div style={{ background: 'rgba(239, 68, 68, 0.1)', color: '#ef4444', padding: '1rem', borderRadius: '0.5rem', marginBottom: '1.5rem', border: '1px solid rgba(239, 68, 68, 0.2)' }}>{err}</div>}

      <div className="card-grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))' }}>
        <div className="glass-card">
          <h3 style={{ margin: '0 0 1.5rem 0' }}>{t("page.intel.search")}</h3>
          <div style={{ display: 'flex', gap: '0.5rem', marginBottom: '1.5rem' }}>
            <input className="input" placeholder={t("page.intel.search.ph")} value={q} onChange={(e) => setQ(e.target.value)} />
            <button className="btn" onClick={search}>{t("dir.search.btn")}</button>
          </div>
          {results && Object.entries(results).map(([k, v]) => (
            <div key={k} style={{ marginBottom: '1rem' }}>
              <b style={{ color: 'var(--primary)' }}>{k}</b>
              <ul style={{ margin: '0.5rem 0', paddingLeft: '1.2rem', color: 'var(--foreground)' }}>
                {(v as { title: string }[]).map((x, i) => <li key={i}>{x.title}</li>)}
              </ul>
            </div>
          ))}
        </div>

        <div className="glass-card">
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1.5rem' }}>
            <h3 style={{ margin: 0 }}>{t("page.intel.exec")}</h3>
            <button className="btn btn-secondary" onClick={loadOverview}>{t("page.cor.load")}</button>
          </div>
          <pre style={{ background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', overflowX: 'auto', fontSize: '0.85rem' }}>
            {overview && JSON.stringify(overview, null, 2)}
          </pre>
        </div>

        <div className="glass-card">
          <h3 style={{ margin: '0 0 1.5rem 0' }}>{t("page.intel.ai")}</h3>
          <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem', marginBottom: '1.5rem' }}>
            <input className="input" placeholder={t("page.intel.ask.ph")} value={question} onChange={(e) => setQuestion(e.target.value)} />
            <button className="btn" onClick={ask}>{t("page.intel.ask")}</button>
          </div>
          {answer && (
            <pre style={{ background: 'rgba(59, 130, 246, 0.1)', border: '1px solid rgba(59, 130, 246, 0.2)', padding: '1rem', borderRadius: '0.5rem', whiteSpace: "pre-wrap", fontFamily: 'inherit', lineHeight: 1.6 }}>
              {answer}
            </pre>
          )}
        </div>

        {/* Folderit ISO Document Control & Retention Disposition */}
        <div className="glass-card" style={{ gridColumn: '1 / -1' }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '1rem' }}>
            <div>
              <h3 style={{ margin: 0 }}>🛡️ ISO Document Control & Retention Audit (Folderit Pattern)</h3>
              <p style={{ margin: '0.25rem 0 0 0', opacity: 0.7, fontSize: '0.85rem' }}>
                ISO 9001:2015 & ISO 27001 Document Control • Periodic Recertification Reviews • Automated Disposition Triggers
              </p>
            </div>
            <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
              <button className="btn btn-secondary" onClick={loadDocumentControl} style={{ fontSize: '0.85rem' }}>
                🔄 Load Live Rules & Reviews
              </button>
              <span className="badge" style={{ background: 'rgba(16, 185, 129, 0.2)', color: '#10b981' }}>ISO Compliance Active</span>
            </div>
          </div>

          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(240px, 1fr))', gap: '1rem', marginTop: '1rem' }}>
            <div style={{ padding: '1rem', background: 'var(--background)', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
              <div style={{ fontSize: '0.8rem', opacity: 0.7 }}>Accreditation Retention Rule</div>
              <div style={{ fontWeight: 700, fontSize: '1.1rem', marginTop: '0.2rem' }}>10-Year Permanent Archive</div>
              <div style={{ fontSize: '0.75rem', color: '#10b981', marginTop: '0.4rem' }}>HEC & ISO Clause 7.5 Compliance</div>
            </div>

            <div style={{ padding: '1rem', background: 'var(--background)', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
              <div style={{ fontSize: '0.8rem', opacity: 0.7 }}>Periodic Review Interval</div>
              <div style={{ fontWeight: 700, fontSize: '1.1rem', marginTop: '0.2rem' }}>Every 12 Months</div>
              <div style={{ fontSize: '0.75rem', color: 'var(--primary)', marginTop: '0.4rem' }}>Automated Review Queue Trigger</div>
            </div>

            <div style={{ padding: '1rem', background: 'var(--background)', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
              <div style={{ fontSize: '0.8rem', opacity: 0.7 }}>Disposition Protocol</div>
              <div style={{ fontWeight: 700, fontSize: '1.1rem', marginTop: '0.2rem' }}>Cryptographic SHA-256 Lock</div>
              <div style={{ fontSize: '0.75rem', color: '#eab308', marginTop: '0.4rem' }}>Tamper-Evident Version Seals</div>
            </div>
          </div>

          {/* Interactive Action Rules (Odoo Documents rule pattern) */}
          <div style={{ marginTop: '1.5rem', paddingTop: '1.5rem', borderTop: '1px solid var(--border)', display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(320px, 1fr))', gap: '1.5rem' }}>
            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                <h4 style={{ margin: 0, fontSize: '1rem' }}>⚡ Automated Action Rules (Odoo Documents)</h4>
                <span className="badge" style={{ fontSize: '0.75rem' }}>{actionRules.length} Active</span>
              </div>
              <p style={{ fontSize: '0.85rem', opacity: 0.7, margin: '0 0 1rem 0' }}>
                When a document is tagged, matching active rules automatically trigger ISO retention policies or review activities.
              </p>

              {actionRules.length === 0 ? (
                <div style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', opacity: 0.6, fontSize: '0.85rem' }}>
                  No action rules loaded. Click &quot;Load Live Rules &amp; Reviews&quot; above.
                </div>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                  {actionRules.map((rule) => (
                    <div key={rule.id} style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', border: '1px solid var(--border)', display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
                      <div>
                        <div style={{ fontWeight: 600, fontSize: '0.9rem' }}>
                          {rule.actionType}: <span className="badge">[{rule.triggerCategory}:{rule.triggerValue}]</span>
                        </div>
                        <div style={{ fontSize: '0.75rem', opacity: 0.7, marginTop: '0.2rem' }}>
                          Config: {rule.targetValue?.slice(0, 50)}...
                        </div>
                      </div>
                      <button className="btn btn-secondary" style={{ padding: '0.3rem 0.6rem', fontSize: '0.75rem', color: '#ef4444' }} onClick={() => deleteActionRule(rule.id)}>
                        Delete
                      </button>
                    </div>
                  ))}
                </div>
              )}

              {/* Add Rule Form */}
              <div style={{ marginTop: '1rem', background: 'var(--background)', padding: '1rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                <h5 style={{ margin: '0 0 0.75rem 0', fontSize: '0.9rem' }}>+ Create Automated Rule</h5>
                <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                  <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.5rem' }}>
                    <input className="input" style={{ fontSize: '0.85rem' }} placeholder="Category (e.g. Tag)" value={ruleCategory} onChange={(e) => setRuleCategory(e.target.value)} />
                    <input className="input" style={{ fontSize: '0.85rem' }} placeholder="Value (e.g. Accreditation)" value={ruleValue} onChange={(e) => setRuleValue(e.target.value)} />
                  </div>
                  <select className="input" style={{ fontSize: '0.85rem' }} value={ruleActionType} onChange={(e) => setRuleActionType(e.target.value)}>
                    <option value="AutoRetention">AutoRetention (Apply ISO Retention Policy)</option>
                    <option value="ScheduleReviewActivity">ScheduleReviewActivity (Dispatch Compliance Task)</option>
                  </select>
                  <input className="input" style={{ fontSize: '0.85rem' }} placeholder="Config JSON" value={ruleTargetValue} onChange={(e) => setRuleTargetValue(e.target.value)} />
                  <button className="btn" style={{ fontSize: '0.85rem', marginTop: '0.25rem' }} onClick={createActionRule}>
                    Save Rule
                  </button>
                </div>
              </div>
            </div>

            {/* Periodic Retention Compliance Queue (Folderit) */}
            <div>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '0.75rem' }}>
                <h4 style={{ margin: 0, fontSize: '1rem' }}>📅 Due Recertification Queue (Folderit)</h4>
                <span className="badge" style={{ fontSize: '0.75rem', background: 'rgba(234, 179, 8, 0.2)', color: '#eab308' }}>Audit Queue</span>
              </div>
              <p style={{ fontSize: '0.85rem', opacity: 0.7, margin: '0 0 1rem 0' }}>
                Documents that reached their periodic review interval and require compliance sign-off or permanent disposal.
              </p>

              {reviews.length === 0 ? (
                <div style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', opacity: 0.6, fontSize: '0.85rem' }}>
                  No documents currently due for periodic review.
                </div>
              ) : (
                <div style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                  {reviews.map((item) => (
                    <div key={item.policy.id} style={{ background: 'var(--background)', padding: '0.75rem', borderRadius: '0.5rem', border: '1px solid var(--border)' }}>
                      <div style={{ fontWeight: 600, fontSize: '0.9rem' }}>
                        {item.document?.title ?? `Document ${item.policy.documentId}`}
                      </div>
                      <div style={{ display: 'flex', gap: '0.5rem', fontSize: '0.75rem', opacity: 0.8, marginTop: '0.25rem' }}>
                        <span>Standard: <b>{item.policy.standard}</b></span>
                        <span>Action: <b className="badge">{item.policy.dispositionAction}</b></span>
                      </div>
                      <div style={{ fontSize: '0.75rem', color: '#ef4444', marginTop: '0.25rem' }}>
                        Review Due: {new Date(item.policy.nextReviewDueAt).toLocaleDateString()}
                      </div>
                    </div>
                  ))}
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
