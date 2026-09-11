"use client";
import { useState } from "react";

export default function Home() {
  const [rtl, setRtl] = useState(false);
  return (
    <main dir={rtl ? "rtl" : "ltr"} style={{ padding: 32, maxWidth: 720 }}>
      <h1>EduNexus OS V2 — R1 Foundation</h1>
      <p>Tenant · Organization · Identity · RBAC · Directory · Audit</p>
      <button onClick={() => setRtl(!rtl)}>
        Switch to {rtl ? "LTR" : "RTL (Arabic)"}
      </button>
      <ul>
        <li>API health: <code>GET /health</code></li>
        <li>Tenants: <code>GET/POST /api/tenants</code></li>
        <li><a href="/directory">Directory (UX-DIR-001)</a></li>
        <li><a href="/roles">Roles & assignments (UX-ADM-001)</a></li>
        <li><a href="/audit">Audit trail</a></li>
        <li>Metrics: <code>GET /metrics</code> → Prometheus :9091 → Grafana :3001</li>
      </ul>
      <p>Docs: <code>docs/01..11</code> · Traceability: <code>docs/11-traceability/matrix.csv</code></p>
    </main>
  );
}