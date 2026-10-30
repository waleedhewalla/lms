import { config } from "./config";
import { permissionsOf, personIdOf } from "./modules";
import { claims } from "./oidc";
import { browserStorage, loadSnapshot } from "./offlineStore";

/** The only workspace page that opens from the device's copy when the network is gone. */
export const OFFLINE_PATH = "/my-work";

export type OfflineSession = { name: string; perms: string[]; personId: string };

/**
 * Access tokens are short-lived and cannot be renewed without the network, so offline the expired token would send
 * the person to a sign-in page that cannot load. When the deployment keeps an offline copy, and this device really is
 * offline, and the copy belongs to the person in the stored token, My Work may open from that copy. Nothing is sent to
 * the server in this state: queued actions wait until the person has signed in again and confirms them.
 */
export async function offlineSession(now = new Date()): Promise<OfflineSession | null> {
  if (!config().offlineData || typeof navigator === "undefined" || navigator.onLine !== false) return null;
  const c = claims({ ignoreExpiry: true });
  const personId = personIdOf(c);
  if (!c || !personId) return null;
  const snap = await loadSnapshot(browserStorage(), now, personId);
  return snap ? { name: snap.personName, perms: permissionsOf(c), personId } : null;
}
