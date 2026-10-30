/** Helpers for the personal calendar subscription link (kept apart from the page so they can be unit-tested). */

export type FeedStatus = { active: boolean; createdAt: string | null; expiresAt: string | null; lastUsedAt: string | null };

/** The full address of a feed from the API's base address and the path the API returned. */
export const feedUrl = (apiBase: string, path: string) => `${apiBase.replace(/\/$/, "")}${path}`;

/** The same address with the scheme calendar apps recognise as "subscribe" (webcal for http, webcals for https). */
export const webcalUrl = (url: string) => url.replace(/^https:\/\//i, "webcals://").replace(/^http:\/\//i, "webcal://");

/** A link that is not https can be read by anyone on the network path: warn before it is pasted into a calendar app. */
export const isInsecure = (url: string) => /^http:\/\//i.test(url);
