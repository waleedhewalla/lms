// Runtime configuration for the browser, read from the server environment at request time so one
// image serves every environment (no rebuild to change the API URL or identity provider).
export const dynamic = "force-dynamic";

export function GET() {
  const config = {
    apiBase: process.env.EDUNEXUS_API_BASE || process.env.NEXT_PUBLIC_API_BASE || "http://127.0.0.1:5238",
    oidcAuthority: process.env.EDUNEXUS_OIDC_AUTHORITY || "",
    oidcClientId: process.env.EDUNEXUS_OIDC_CLIENT_ID || "edunexus-web",
    oidcScope: process.env.EDUNEXUS_OIDC_SCOPE || "openid profile email",
    // Operations dashboard link in the header; hidden when unset.
    grafanaUrl: process.env.EDUNEXUS_GRAFANA_URL ?? (process.env.NODE_ENV === "production" ? "" : "http://localhost:3021"),
  };
  return new Response(`window.__EDUNEXUS__=${JSON.stringify(config)};`, {
    headers: { "Content-Type": "application/javascript; charset=utf-8", "Cache-Control": "no-store" },
  });
}
