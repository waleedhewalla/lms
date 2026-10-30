/**
 * The device-side copy of "My Work" for offline use. Deliberately small: counts, what is waiting for the person to approve
 * (type, id, due date, priority) and their open tasks. No letter text, no notification bodies, no tokens. One person's copy
 * at a time; it is removed on sign-out, when someone else signs in, after SNAPSHOT_MAX_AGE_MS, and on request.
 * Only used when the deployment turns on EDUNEXUS_OFFLINE_DATA.
 */

export type OfflineStorage = {
  get<T>(key: string): Promise<T | undefined>;
  put(key: string, value: unknown): Promise<void>;
  remove(key: string): Promise<void>;
  keys(): Promise<string[]>;
  clear(): Promise<void>;
};

export const SNAPSHOT_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
const SNAPSHOT_KEY = "snapshot";
export const QUEUE_PREFIX = "queue:";

export type SnapshotApproval = { id: string; entityType: string; dueAt: string; priority: string };
export type SnapshotTask = { id: string; title: string; status: string; dueAt: string; priority: string; progress: number };
export type Snapshot = {
  tenantId: string; personId: string; personName: string; savedAt: string;
  counts: { approvals: number; tasks: number; overdue: number } | null;
  approvals: SnapshotApproval[]; tasks: SnapshotTask[];
};

export async function saveSnapshot(storage: OfflineStorage, s: Snapshot): Promise<void> {
  // Pick fields explicitly so nothing else on a server object can end up on the device.
  const clean: Snapshot = {
    tenantId: s.tenantId, personId: s.personId, personName: s.personName, savedAt: s.savedAt,
    counts: s.counts ? { approvals: s.counts.approvals, tasks: s.counts.tasks, overdue: s.counts.overdue } : null,
    approvals: s.approvals.map((a) => ({ id: a.id, entityType: a.entityType, dueAt: a.dueAt, priority: a.priority })),
    tasks: s.tasks.map((t) => ({ id: t.id, title: t.title, status: t.status, dueAt: t.dueAt, priority: t.priority, progress: t.progress })),
  };
  await storage.put(SNAPSHOT_KEY, clean);
}

/** The saved copy, or undefined when there is none, it is too old (then it is removed), or it belongs to someone else. */
export async function loadSnapshot(storage: OfflineStorage, now: Date, personId?: string): Promise<Snapshot | undefined> {
  const s = await storage.get<Snapshot>(SNAPSHOT_KEY);
  if (!s) return undefined;
  if (now.getTime() - new Date(s.savedAt).getTime() > SNAPSHOT_MAX_AGE_MS) { await purgeAll(storage); return undefined; }
  if (personId && s.personId !== personId) return undefined;
  return s;
}

/** Everything this feature keeps on the device: the copy and every queued action. */
export async function purgeAll(storage: OfflineStorage): Promise<void> {
  await storage.clear();
}

/** When a different person signs in, the previous person's copy and queued actions are removed, never carried over. */
export async function purgeIfOtherPerson(storage: OfflineStorage, personId: string | null): Promise<boolean> {
  const s = await storage.get<Snapshot>(SNAPSHOT_KEY);
  const queued = (await storage.keys()).filter((k) => k.startsWith(QUEUE_PREFIX));
  const owners = new Set<string>();
  if (s) owners.add(s.personId);
  for (const k of queued) { const q = await storage.get<{ personId: string }>(k); if (q) owners.add(q.personId); }
  if (owners.size === 0 || (owners.size === 1 && personId !== null && owners.has(personId))) return false;
  await purgeAll(storage);
  return true;
}

/** In-memory storage: for tests, and as the fallback where IndexedDB is unavailable (nothing then outlives the page). */
export function memoryStorage(): OfflineStorage {
  const data = new Map<string, unknown>();
  return {
    get: async <T,>(k: string) => (data.has(k) ? structuredClone(data.get(k)) as T : undefined),
    put: async (k, v) => { data.set(k, structuredClone(v)); },
    remove: async (k) => { data.delete(k); },
    keys: async () => [...data.keys()],
    clear: async () => { data.clear(); },
  };
}

const DB_NAME = "edunexus-offline";
const STORE = "kv";

/** IndexedDB storage (browser only). Falls back to memory when the browser refuses IndexedDB, e.g. in a private window. */
export function browserStorage(): OfflineStorage {
  if (typeof indexedDB === "undefined") return memoryStorage();
  const open = () => new Promise<IDBDatabase>((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => { req.result.createObjectStore(STORE); };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
  const run = async <T,>(mode: IDBTransactionMode, work: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> => {
    const db = await open();
    try {
      return await new Promise<T>((resolve, reject) => {
        const tx = db.transaction(STORE, mode);
        const req = work(tx.objectStore(STORE));
        tx.oncomplete = () => resolve(req.result);
        tx.onerror = () => reject(tx.error);
        tx.onabort = () => reject(tx.error);
      });
    } finally { db.close(); }
  };
  return {
    get: <T,>(k: string) => run("readonly", (s) => s.get(k)) as Promise<T | undefined>,
    put: async (k, v) => { await run("readwrite", (s) => s.put(v, k)); },
    remove: async (k) => { await run("readwrite", (s) => s.delete(k)); },
    keys: async () => (await run("readonly", (s) => s.getAllKeys())).map(String),
    clear: async () => { await run("readwrite", (s) => s.clear()); },
  };
}
