"use client";
import { useState } from "react";
import { useTranslation } from "./TranslationProvider";

export function ConnectionBar({ tenantId, setTenantId }: { tenantId: string; setTenantId: (v: string) => void }) {
  const { t } = useTranslation();
  const [token, setToken] = useState("");
  const [status, setStatus] = useState("");
  
  async function mint() {
    setStatus("…");
    try {
      const res = await fetch("http://127.0.0.1:5238/api/auth/dev-token", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          subject: "web-admin",
          tenantId,
          permissions: ["tenant:read", "org:read", "person:read", "person:create", "role:create", "role:assign", "role:read", "audit:read", "correspondence:create", "correspondence:read", "correspondence:confidential", "approval:read", "approval:decide", "task:read", "task:update", "notification:read", "committee:create", "committee:read", "meeting:create", "meeting:read", "decision:create", "decision:read", "action:update", "policy:create", "policy:read", "policy:ack", "document:create", "document:read", "search:read", "analytics:read", "quality:manage", "strategy:manage", "ai:manage", "ai:ask", "ai:read", "integration:manage", "integration:read"],
        }),
      });
      if (!res.ok) throw new Error(`dev-token → ${res.status} (API must run in Development)`);
      const data = await res.json();
      window.localStorage.setItem("edunexus.token", data.token);
      setToken(data.token.slice(0, 24) + "…");
      setStatus("connected");
    } catch (e) {
      setStatus(String(e));
    }
  }
  
  return (
    <div className="glass-card" style={{ marginBottom: '2rem' }}>
      <h3 style={{ marginBottom: '1rem', display: 'flex', alignItems: 'center', gap: '0.5rem' }}>
        <span style={{ fontSize: '1.2rem' }}>🔑</span> {t("conn.title")}
      </h3>
      <div style={{ display: 'flex', gap: '1rem', alignItems: 'flex-end', flexWrap: 'wrap' }}>
        <div className="form-group" style={{ flex: 1, minWidth: '300px', margin: 0 }}>
          <label>{t("conn.tenant")}</label>
          <input 
            className="input" 
            value={tenantId} 
            onChange={(e) => setTenantId(e.target.value)} 
            placeholder={t("conn.placeholder")}
          />
        </div>
        <button className="btn" onClick={mint}>{t("conn.btn")}</button>
      </div>
      {(token || status) && (
        <div style={{ marginTop: '1rem', fontSize: '0.9rem', opacity: 0.8 }}>
          <span className="badge" style={{ margin: 0 }}>{status}</span> {token && <span style={{ marginLeft: '0.5rem', fontFamily: 'monospace' }}>{token}</span>}
        </div>
      )}
    </div>
  );
}
