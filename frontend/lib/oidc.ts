// OpenID Connect Authorization Code flow with PKCE for a public browser client. No client secret,
// no third-party library: discovery → authorize redirect → code exchange at the token endpoint.
import { config } from "./config";

const TOKEN_KEY = "edunexus.token";
const ID_TOKEN_KEY = "edunexus.idToken";
const PENDING_KEY = "edunexus.oidc.pending";

type Discovery = { authorization_endpoint: string; token_endpoint: string; end_session_endpoint?: string };

async function discover(): Promise<Discovery> {
  const authority = config().oidcAuthority.replace(/\/$/, "");
  const res = await fetch(`${authority}/.well-known/openid-configuration`);
  if (!res.ok) throw new Error(`OIDC discovery → ${res.status}`);
  return res.json();
}

function base64Url(bytes: Uint8Array): string {
  let s = "";
  bytes.forEach((b) => (s += String.fromCharCode(b)));
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

const random = (n = 32) => base64Url(crypto.getRandomValues(new Uint8Array(n)));
const redirectUri = () => `${window.location.origin}/auth/callback`;

export async function signIn(returnTo = "/my-work"): Promise<void> {
  const d = await discover();
  const verifier = random(48);
  const challenge = base64Url(new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier))));
  const state = random(16);
  sessionStorage.setItem(PENDING_KEY, JSON.stringify({ verifier, state, returnTo }));
  const c = config();
  const q = new URLSearchParams({
    response_type: "code", client_id: c.oidcClientId, redirect_uri: redirectUri(), scope: c.oidcScope,
    state, code_challenge: challenge, code_challenge_method: "S256",
  });
  window.location.assign(`${d.authorization_endpoint}?${q}`);
}

/** Completes the redirect from the IdP; returns the path to continue to. */
export async function completeSignIn(search: string): Promise<string> {
  const params = new URLSearchParams(search);
  if (params.get("error")) throw new Error(`${params.get("error")}: ${params.get("error_description") ?? ""}`);
  const pending = JSON.parse(sessionStorage.getItem(PENDING_KEY) ?? "null") as { verifier: string; state: string; returnTo: string } | null;
  sessionStorage.removeItem(PENDING_KEY);
  if (!pending || pending.state !== params.get("state")) throw new Error("Sign-in state mismatch — start again.");
  const d = await discover();
  const res = await fetch(d.token_endpoint, {
    method: "POST",
    headers: { "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code", code: params.get("code") ?? "", redirect_uri: redirectUri(),
      client_id: config().oidcClientId, code_verifier: pending.verifier,
    }),
  });
  if (!res.ok) throw new Error(`token exchange → ${res.status}`);
  const tokens = await res.json() as { access_token: string; id_token?: string };
  localStorage.setItem(TOKEN_KEY, tokens.access_token);
  if (tokens.id_token) localStorage.setItem(ID_TOKEN_KEY, tokens.id_token);
  const tenant = claims()?.tenant_id;
  if (typeof tenant === "string") localStorage.setItem("edunexus.tenant", tenant);
  // Same-origin paths only: "//host" and "/\\host" would be treated as other origins by the browser.
  const safe = /^\/(?![\/\\])/.test(pending.returnTo);
  return safe ? pending.returnTo : "/my-work";
}

export async function signOut(): Promise<void> {
  const idToken = localStorage.getItem(ID_TOKEN_KEY);
  localStorage.removeItem(TOKEN_KEY);
  localStorage.removeItem(ID_TOKEN_KEY);
  if (!config().oidcAuthority) { window.location.assign("/"); return; }
  const d = await discover().catch(() => null);
  if (!d?.end_session_endpoint) { window.location.assign("/"); return; }
  const q = new URLSearchParams({ client_id: config().oidcClientId, post_logout_redirect_uri: window.location.origin });
  if (idToken) q.set("id_token_hint", idToken);
  window.location.assign(`${d.end_session_endpoint}?${q}`);
}

/** Decoded payload of the stored access token (display only — the API validates the signature). */
export function claims(): Record<string, unknown> | null {
  try {
    const token = localStorage.getItem(TOKEN_KEY);
    if (!token) return null;
    const part = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
    const json = decodeURIComponent(atob(part).split("").map((c) => "%" + c.charCodeAt(0).toString(16).padStart(2, "0")).join(""));
    const payload = JSON.parse(json) as Record<string, unknown>;
    if (typeof payload.exp === "number" && payload.exp * 1000 < Date.now()) return null;
    return payload;
  } catch {
    return null;
  }
}
