"use client";
import { ReactNode, useEffect, useState } from "react";
import { useTranslation } from "./TranslationProvider";
import { ConnectionBar } from "./ConnectionBar";

/** Inline bilingual text: L("Save", "حفظ"). */
export function useL() {
  const { locale } = useTranslation();
  return (en: string, ar: string) => (locale === "ar" ? ar : en);
}

/** Tenant id shared across pages (remembered in localStorage). */
export function useTenant(): [string, (v: string) => void] {
  const [tenantId, setTenant] = useState("");
  useEffect(() => {
    try { setTenant(window.localStorage.getItem("edunexus.tenant") ?? ""); } catch { /* storage unavailable */ }
  }, []);
  const set = (v: string) => {
    setTenant(v);
    try { window.localStorage.setItem("edunexus.tenant", v); } catch { /* storage unavailable */ }
  };
  return [tenantId, set];
}

export function PageHeader({ title, code, actions }: { title: string; code?: string; actions?: ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", marginBottom: "2rem", gap: "1rem", flexWrap: "wrap" }}>
      <div>
        <h1 style={{ margin: 0, fontSize: "2rem" }}>{title}</h1>
        {code && <p style={{ color: "var(--primary)", fontWeight: 600, marginTop: "0.5rem" }}>{code}</p>}
      </div>
      <div style={{ display: "flex", gap: "0.5rem", flexWrap: "wrap" }}>{actions}</div>
    </div>
  );
}

/** Page shell: header, connection bar (tenant + token) and an error box. */
export function Page({ title, code, actions, tenantId, setTenantId, error, children }: {
  title: string; code?: string; actions?: ReactNode; tenantId: string; setTenantId: (v: string) => void; error?: string; children: ReactNode;
}) {
  return (
    <div>
      <PageHeader title={title} code={code} actions={actions} />
      <ConnectionBar tenantId={tenantId} setTenantId={setTenantId} />
      {error && <ErrorBox message={error} />}
      {children}
    </div>
  );
}

export function ErrorBox({ message }: { message: string }) {
  return (
    <div role="alert" style={{ background: "rgba(239, 68, 68, 0.1)", color: "#ef4444", padding: "1rem", borderRadius: "0.5rem",
      marginBottom: "1.5rem", border: "1px solid rgba(239, 68, 68, 0.2)" }}>{message}</div>
  );
}

export function Card({ title, children, wide }: { title?: string; children: ReactNode; wide?: boolean }) {
  return (
    <div className="glass-card" style={wide ? { gridColumn: "1 / -1" } : undefined}>
      {title && <h3 style={{ margin: "0 0 1rem 0" }}>{title}</h3>}
      {children}
    </div>
  );
}

export function Kpi({ label, value, hint }: { label: string; value: ReactNode; hint?: string }) {
  return (
    <div className="glass-card" style={{ padding: "1.25rem" }}>
      <div style={{ fontSize: "0.85rem", opacity: 0.75 }}>{label}</div>
      <div style={{ fontSize: "2rem", fontWeight: 700, marginTop: "0.25rem" }}>{value ?? "–"}</div>
      {hint && <div style={{ fontSize: "0.8rem", opacity: 0.6 }}>{hint}</div>}
    </div>
  );
}

export function Table<T>({ rows, columns, empty, onRowClick }: {
  rows: T[]; columns: { label: string; render: (r: T) => ReactNode }[]; empty: string; onRowClick?: (r: T) => void;
}) {
  if (rows.length === 0) return <p style={{ opacity: 0.7 }}>{empty}</p>;
  return (
    <div style={{ overflowX: "auto" }}>
      <table style={{ width: "100%", borderCollapse: "collapse", fontSize: "0.9rem" }}>
        <thead>
          <tr>{columns.map((c) => <th key={c.label} style={{ textAlign: "start", padding: "0.5rem", borderBottom: "1px solid rgba(148,163,184,0.3)" }}>{c.label}</th>)}</tr>
        </thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={i} onClick={onRowClick ? () => onRowClick(r) : undefined} style={{ cursor: onRowClick ? "pointer" : undefined }}>
              {columns.map((c) => <td key={c.label} style={{ padding: "0.5rem", borderBottom: "1px solid rgba(148,163,184,0.15)" }}>{c.render(r)}</td>)}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="form-group" style={{ margin: 0 }}>
      <label>{label}</label>
      {children}
    </div>
  );
}

export const fmtDate = (s?: string | null) => (s ? new Date(s).toLocaleString() : "–");

/** Runs an async action, reporting errors through setError. */
export async function attempt(setError: (e: string) => void, fn: () => Promise<void>) {
  setError("");
  try { await fn(); } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
}
