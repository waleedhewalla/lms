/**
 * Actions taken while offline (approve, reject, complete a task), kept until the person reviews and sends them.
 * The server decides again when they arrive, exactly as for an action taken online, so a queued action can never do
 * more than the person could do at that moment.
 */
import { QUEUE_PREFIX, type OfflineStorage } from "./offlineStore";

export type QueueKind = "approve" | "reject" | "complete";
export type QueueItem = { id: string; kind: QueueKind; targetId: string; label: string; tenantId: string; personId: string; createdAt: string };

export async function enqueue(storage: OfflineStorage, item: Omit<QueueItem, "id" | "createdAt">, now: Date, id = crypto.randomUUID()): Promise<QueueItem> {
  const full: QueueItem = { ...item, id, createdAt: now.toISOString() };
  await storage.put(QUEUE_PREFIX + id, full);
  return full;
}

/** Oldest first: actions are sent in the order they were taken. */
export async function listQueue(storage: OfflineStorage): Promise<QueueItem[]> {
  const items: QueueItem[] = [];
  for (const k of (await storage.keys()).filter((x) => x.startsWith(QUEUE_PREFIX))) {
    const q = await storage.get<QueueItem>(k);
    if (q) items.push(q);
  }
  return items.sort((a, b) => a.createdAt.localeCompare(b.createdAt));
}

export async function discardQueue(storage: OfflineStorage): Promise<void> {
  for (const k of (await storage.keys()).filter((x) => x.startsWith(QUEUE_PREFIX))) await storage.remove(k);
}

/** The HTTP status the server answered, or 0 when it could not be reached. */
export type Send = (item: QueueItem) => Promise<number>;

export type DropReason = "already-done" | "not-allowed" | "not-found" | "other-person" | "rejected";
export type ReplayResult = {
  sent: QueueItem[];
  dropped: { item: QueueItem; reason: DropReason }[];
  kept: QueueItem[];
  /** Set when sending stopped early: the person must sign in again (401) or the server could not be reached. */
  stoppedBy: "unauthorized" | "unreachable" | "busy" | null;
};

/** Which server answers mean "this item is finished, one way or another" (it is then removed and never retried). */
function outcome(status: number): "sent" | DropReason | "retry" | "unauthorized" {
  if (status >= 200 && status < 300) return "sent";
  if (status === 409) return "already-done";
  if (status === 403) return "not-allowed";
  if (status === 404) return "not-found";
  if (status === 401) return "unauthorized";
  if (status === 0 || status >= 500 || status === 408 || status === 429) return "retry";
  return "rejected";   // any other 4xx: the server will not accept it, so retrying cannot help
}

let inFlight = false;

/** Runs `work` unless another replay is already running here or in another tab (Web Locks where available). */
async function exclusive<T>(work: () => Promise<T>): Promise<T | "busy"> {
  const locks = (typeof navigator !== "undefined" ? (navigator as Navigator & { locks?: LockManager }).locks : undefined);
  if (locks) {
    const result = await locks.request("edunexus-offline-replay", { ifAvailable: true }, async (lock) => (lock ? work() : "busy" as const));
    return result;
  }
  if (inFlight) return "busy";
  inFlight = true;
  try { return await work(); } finally { inFlight = false; }
}

/**
 * Sends the queue in order. Items that belong to another person are dropped (never sent). A 401 or an unreachable server
 * stops the run and keeps everything not yet sent.
 */
export async function replay(storage: OfflineStorage, send: Send, personId: string): Promise<ReplayResult> {
  const res = await exclusive(async (): Promise<ReplayResult> => {
    const result: ReplayResult = { sent: [], dropped: [], kept: [], stoppedBy: null };
    const items = await listQueue(storage);
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      if (item.personId !== personId) { result.dropped.push({ item, reason: "other-person" }); await storage.remove(QUEUE_PREFIX + item.id); continue; }
      const o = outcome(await send(item));
      if (o === "sent") { result.sent.push(item); await storage.remove(QUEUE_PREFIX + item.id); continue; }
      if (o === "unauthorized" || o === "retry") {
        result.stoppedBy = o === "unauthorized" ? "unauthorized" : "unreachable";
        result.kept.push(...items.slice(i));
        return result;
      }
      result.dropped.push({ item, reason: o });
      await storage.remove(QUEUE_PREFIX + item.id);
    }
    return result;
  });
  return res === "busy" ? { sent: [], dropped: [], kept: await listQueue(storage), stoppedBy: "busy" } : res;
}
