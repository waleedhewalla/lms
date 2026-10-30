"use client";
import { useEffect, useState } from "react";
import { apiBase, oidcEnabled } from "../lib/config";
import { claims, signIn, signOut } from "../lib/oidc";
import { DEV_PERMISSIONS } from "../lib/devToken";
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
                    permissions: DEV_PERMISSIONS,
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
  
  // With single sign-on, identity and tenant come from the token and the header shows the account: nothing to show here.
  if (sso) return null;

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
