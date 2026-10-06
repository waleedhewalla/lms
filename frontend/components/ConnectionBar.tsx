"use client";
import { useEffect, useState } from "react";
import { apiBase, oidcEnabled } from "../lib/config";
import { claims, signIn, signOut } from "../lib/oidc";
import { useTranslation } from "./TranslationProvider";

export function ConnectionBar({ tenantId, setTenantId }: { tenantId: string; setTenantId: (v: string) => void }) {
  const { t } = useTranslation();
  const [token, setToken] = useState("");
  const [status, setStatus] = useState("");
  const [personId, setPersonId] = useState("");
  const [sso, setSso] = useState(false);
  const [user, setUser] = useState<string | null>(null);
  useEffect(() => {
    setSso(oidcEnabled());
    const c = claims();
    setUser(c ? String(c.name ?? c.preferred_username ?? c.email ?? c.sub ?? "") : null);
  }, []);
  
  async function mint() {
    setStatus("…");
    try {
      const res = await fetch(`${apiBase()}/api/auth/dev-token`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          subject: "web-admin",
          tenantId,
          personId: personId.trim() || null,
          permissions: ["tenant:read", "org:read", "person:read", "person:create", "role:create", "role:assign", "role:read", "audit:read", "correspondence:create", "correspondence:read", "correspondence:confidential", "approval:read", "approval:decide", "task:read", "task:update", "notification:read", "committee:create", "committee:read", "meeting:create", "meeting:read", "decision:create", "decision:read", "action:update", "policy:create", "policy:read", "policy:ack", "document:create", "document:read", "search:read", "analytics:read", "quality:manage", "strategy:manage", "ai:manage", "ai:ask", "ai:read", "integration:manage", "integration:read", "inbox:read", "quality:read", "strategy:read", "communication:create", "notification:manage", "form:manage", "request:create", "request:read", "workflow:manage", "workflow:read", "document:update", "document:manage", "chatter:read", "chatter:write", "activity:read", "activity:write", "task:create", "task:verify", "person:update", "communication:read", "form:read", "calendar:read", "calendar:manage", "minutes:approve", "action:verify", "sla:manage", "meeting:update", "analytics:read"],
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
  
  if (sso) {
    // Production: identity comes from the institution's IdP; the tenant comes from the token's tenant_id claim.
    return (
      <div className="glass-card" style={{ marginBottom: '2rem', display: 'flex', gap: '1rem', alignItems: 'center', flexWrap: 'wrap' }}>
        <span style={{ fontSize: '1.2rem' }}>🔑</span>
        {user
          ? <><span>{user}</span><button className="btn btn-secondary" onClick={() => signOut()}>Sign out / تسجيل الخروج</button></>
          : <button className="btn" onClick={() => signIn(window.location.pathname).catch((e) => setStatus(String(e)))}>Sign in / تسجيل الدخول</button>}
        {status && <span role="alert" className="badge" style={{ margin: 0 }}>{status}</span>}
      </div>
    );
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
        <div className="form-group" style={{ flex: 1, minWidth: '300px', margin: 0 }}>
          <label>{t("conn.person")}</label>
          <input
            className="input"
            value={personId}
            onChange={(e) => setPersonId(e.target.value)}
            placeholder={t("conn.personPlaceholder")}
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
