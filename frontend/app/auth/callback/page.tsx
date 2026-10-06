"use client";
import { useEffect, useState } from "react";
import { completeSignIn } from "../../../lib/oidc";

export default function AuthCallback() {
  const [error, setError] = useState("");
  useEffect(() => {
    completeSignIn(window.location.search)
      .then((next) => window.location.replace(next))
      .catch((e) => setError(String(e)));
  }, []);
  return (
    <div className="glass-card" style={{ maxWidth: 520, margin: "4rem auto" }}>
      {error
        ? <><p role="alert">{error}</p><a className="btn" href="/">Back / رجوع</a></>
        : <p>Signing you in… / جارٍ تسجيل الدخول…</p>}
    </div>
  );
}
