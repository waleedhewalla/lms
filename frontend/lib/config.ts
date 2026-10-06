export type RuntimeConfig = { apiBase: string; oidcAuthority: string; oidcClientId: string; oidcScope: string; grafanaUrl: string };

declare global {
  interface Window { __EDUNEXUS__?: Partial<RuntimeConfig> }
}

const DEFAULTS: RuntimeConfig = {
  apiBase: (typeof process !== "undefined" && process.env.NEXT_PUBLIC_API_BASE) || "http://127.0.0.1:5238",
  oidcAuthority: "",
  oidcClientId: "edunexus-web",
  oidcScope: "openid profile email",
  grafanaUrl: "",
};

/** Config injected by /runtime-config.js (see app/runtime-config.js/route.ts), falling back to build-time defaults. */
export function config(): RuntimeConfig {
  const injected = typeof window !== "undefined" ? window.__EDUNEXUS__ : undefined;
  return { ...DEFAULTS, ...(injected ?? {}) };
}

export const apiBase = () => config().apiBase.replace(/\/$/, "");
export const oidcEnabled = () => config().oidcAuthority !== "";
