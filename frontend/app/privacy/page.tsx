"use client";
import { useEffect, useState } from "react";
import { apiBase } from "../../lib/api";
import { useL } from "../../components/Ui";

type Notice = { institution: string; title: string; content: string; version: number };

/** Public privacy notice (PDPL). Works before sign-in: the institution comes from ?tenant= or the saved tenant. */
export default function PrivacyPage() {
  const L = useL();
  const [notice, setNotice] = useState<Notice | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let tenant = new URLSearchParams(window.location.search).get("tenant") ?? "";
    try { tenant ||= window.localStorage.getItem("edunexus.tenant") ?? ""; } catch { /* storage unavailable */ }
    if (!tenant) { setError("no-tenant"); return; }
    fetch(`${apiBase()}/api/public/privacy-notice?tenant=${encodeURIComponent(tenant)}`)
      .then(async (r) => (r.ok ? setNotice(await r.json()) : setError(r.status === 404 ? "none" : `HTTP ${r.status}`)))
      .catch((e) => setError(String(e)));
  }, []);

  return (
    <article className="glass-card" style={{ maxWidth: 820, margin: "0 auto" }}>
      {notice ? (<>
        <p style={{ margin: 0, opacity: 0.7 }}>{notice.institution}</p>
        <h1 style={{ marginTop: "0.25rem" }}>{notice.title}</h1>
        <p style={{ fontSize: "0.8rem", opacity: 0.7 }}>{L("Version", "الإصدار")} {notice.version}</p>
        <div style={{ whiteSpace: "pre-wrap", lineHeight: 1.7 }}>{notice.content}</div>
      </>) : error === "none" ? (
        <p>{L("This institution has not published a privacy notice yet.", "لم تنشر هذه المؤسسة إشعار الخصوصية بعد.")}</p>
      ) : error === "no-tenant" ? (
        <p>{L("Open this page from your institution's link (…/privacy?tenant=your-institution).", "افتح هذه الصفحة من رابط مؤسستك (‎…/privacy?tenant=اسم-المؤسسة).")}</p>
      ) : error ? (
        <p role="alert">{error}</p>
      ) : (
        <p>{L("Loading…", "جارٍ التحميل…")}</p>
      )}
    </article>
  );
}
