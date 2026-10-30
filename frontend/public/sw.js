/*
 * EduNexus service worker: makes the app installable and shows a friendly page when the network is gone.
 *
 * What it keeps: the offline page, the icons, and the build's static assets (their file names carry a content hash,
 * so a cached copy can never be stale). What it deliberately never keeps: pages, API responses, tokens or anything else
 * that belongs to a person. Personal data on a shared device is a privacy decision, not a caching default.
 */
const VERSION = "v1";
const SHELL = `edunexus-shell-${VERSION}`;
const STATIC = `edunexus-static-${VERSION}`;
const OFFLINE_URL = "/offline.html";
const PRECACHE = [OFFLINE_URL, "/icons/icon-192.png", "/icons/icon-512.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(SHELL).then((cache) => cache.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((names) => Promise.all(names.filter((n) => n.startsWith("edunexus-") && n !== SHELL && n !== STATIC).map((n) => caches.delete(n))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;   // the API and the identity provider are never touched

  if (req.mode === "navigate") {
    // Always ask the network; only when it is unreachable show the offline page.
    event.respondWith(fetch(req).catch(() => caches.match(OFFLINE_URL)));
    return;
  }

  if (url.pathname.startsWith("/_next/static/")) {
    event.respondWith(
      caches.open(STATIC).then(async (cache) => {
        const hit = await cache.match(req);
        if (hit) return hit;
        const res = await fetch(req);
        if (res.ok) cache.put(req, res.clone());
        return res;
      }),
    );
  }
});
