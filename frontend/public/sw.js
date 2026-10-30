/*
 * EduNexus service worker: makes the app installable and shows a friendly page when the network is gone.
 *
 * What it keeps: the offline page, the icons, the build's static assets (their file names carry a content hash, so a
 * cached copy can never be stale), the runtime settings script, and the HTML of the two pages listed in OFFLINE_PAGES.
 * Those pages are prerendered shells with no personal data in them (a test checks the built HTML); a person's own
 * data is fetched from the API and is never stored here. What it deliberately never keeps: API responses, tokens, or
 * any other page. Personal data on a shared device is a privacy decision, not a caching default.
 */
const VERSION = "v2";
const SHELL = `edunexus-shell-${VERSION}`;
const STATIC = `edunexus-static-${VERSION}`;
const PAGES = `edunexus-pages-${VERSION}`;
const OFFLINE_PAGES = ["/my-work", "/dashboard"];
const RUNTIME_CONFIG = "/runtime-config.js";
const OFFLINE_URL = "/offline.html";
const PRECACHE = [OFFLINE_URL, "/icons/icon-192.png", "/icons/icon-512.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(SHELL).then((cache) => cache.addAll(PRECACHE)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((names) => Promise.all(names.filter((n) => n.startsWith("edunexus-") && ![SHELL, STATIC, PAGES].includes(n)).map((n) => caches.delete(n))))
      .then(() => self.clients.claim()),
  );
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;   // the API and the identity provider are never touched

  if (req.mode === "navigate") {
    // Always ask the network. Only when it is unreachable: the saved shell of an allowed page, else the offline page.
    const keep = OFFLINE_PAGES.includes(url.pathname) && !url.search;
    event.respondWith(
      fetch(req).then((res) => {
        if (keep && res.ok && res.status === 200) { const copy = res.clone(); event.waitUntil(caches.open(PAGES).then((c) => c.put(url.pathname, copy))); }
        return res;
      }).catch(async () => (keep && (await (await caches.open(PAGES)).match(url.pathname))) || caches.match(OFFLINE_URL)),
    );
    return;
  }

  if (url.pathname === RUNTIME_CONFIG) {
    // The page cannot start without its settings (API address, offline switch); they hold nothing personal.
    event.respondWith(
      fetch(req).then((res) => {
        if (res.ok) { const copy = res.clone(); event.waitUntil(caches.open(PAGES).then((c) => c.put(RUNTIME_CONFIG, copy))); }
        return res;
      }).catch(async () => (await (await caches.open(PAGES)).match(RUNTIME_CONFIG)) || Response.error()),
    );
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
