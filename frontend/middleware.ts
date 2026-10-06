import { NextResponse } from "next/server";

// Security headers for every page. The CSP is built per request from the runtime config so it allows
// exactly the configured API and identity provider and nothing else. Next's inline bootstrap scripts
// and inline styles need 'unsafe-inline'; no 'unsafe-eval' in production.
const origin = (url: string | undefined) => {
  try { return url ? new URL(url).origin : ""; } catch { return ""; }
};

export function middleware() {
  const api = origin(process.env.EDUNEXUS_API_BASE || process.env.NEXT_PUBLIC_API_BASE || "http://127.0.0.1:5238");
  const idp = origin(process.env.EDUNEXUS_OIDC_AUTHORITY);
  const dev = process.env.NODE_ENV !== "production";
  const csp = [
    "default-src 'self'",
    `script-src 'self' 'unsafe-inline'${dev ? " 'unsafe-eval'" : ""}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    `connect-src 'self' ${api} ${idp}${dev ? " ws:" : ""}`.trim(),
    "frame-ancestors 'none'",
    "base-uri 'self'",
    `form-action 'self' ${idp}`.trim(),
    "object-src 'none'",
  ].join("; ");

  const res = NextResponse.next();
  res.headers.set("Content-Security-Policy", csp);
  res.headers.set("X-Content-Type-Options", "nosniff");
  res.headers.set("X-Frame-Options", "DENY");
  res.headers.set("Referrer-Policy", "no-referrer");
  res.headers.set("Permissions-Policy", "camera=(), microphone=(), geolocation=()");
  return res;
}

export const config = { matcher: ["/((?!_next/static|_next/image|favicon.ico).*)"] };
