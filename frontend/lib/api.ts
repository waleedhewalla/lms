import { apiBase } from "./config";

export { apiBase };

export function authHeaders(): Record<string, string> {
  if (typeof window === "undefined") return { "Content-Type": "application/json" };
  const token = window.localStorage.getItem("edunexus.token") ?? "";
  return { "Content-Type": "application/json", Authorization: `Bearer ${token}` };
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${apiBase()}${path}`, {
    ...init,
    headers: { ...authHeaders(), ...(init?.headers ?? {}) },
  });
  if (res.status === 401) throw new Error(`${init?.method ?? "GET"} ${path} → 401 (not signed in or session expired — sign in again)`);
  if (!res.ok) throw new Error(`${init?.method ?? "GET"} ${path} → ${res.status}`);
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}
