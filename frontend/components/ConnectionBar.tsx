"use client";
import { useState } from "react";

export function ConnectionBar({ tenantId, setTenantId }: { tenantId: string; setTenantId: (v: string) => void }) {
  const [token, setToken] = useState("");
  const [status, setStatus] = useState("");
  async function mint() {
    setStatus("…");
    try {
      const res = await fetch("http://127.0.0.1:5299/api/auth/dev-token", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          subject: "web-admin",
          tenantId,
          permissions: ["tenant:read", "org:read", "person:read", "person:create", "role:create", "role:assign", "role:read", "audit:read"],
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
    <section style={{ border: "1px solid #ccc", padding: 12, marginBottom: 16 }}>
      <h3>Connection (dev)</h3>
      <label>Tenant ID <input value={tenantId} onChange={(e) => setTenantId(e.target.value)} size={40} /></label>{" "}
      <button onClick={mint}>Mint dev token</button>{" "}
      <span>{token} {status}</span>
    </section>
  );
}
