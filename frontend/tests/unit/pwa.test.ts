import { strict as assert } from "node:assert";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import manifest from "../../app/manifest";

const root = join(__dirname, "..", "..");
const sw = readFileSync(join(root, "public", "sw.js"), "utf8");

test("manifest is installable: standalone, in-scope start_url, 192 and 512 PNG icons that exist, one maskable", () => {
  const m = manifest();
  assert.equal(m.display, "standalone");
  assert.ok(String(m.start_url).startsWith(String(m.scope)), "start_url must be inside scope");
  const sizes = (m.icons ?? []).map((i) => i.sizes);
  assert.ok(sizes.includes("192x192") && sizes.includes("512x512"));
  assert.ok((m.icons ?? []).some((i) => i.purpose === "maskable"));
  for (const icon of m.icons ?? []) assert.ok(existsSync(join(root, "public", icon.src)), `${icon.src} is missing`);
});

test("every file the service worker precaches exists", () => {
  const list = /const PRECACHE = \[(.*?)\];/s.exec(sw)?.[1] ?? "";
  const offline = /const OFFLINE_URL = "([^"]+)"/.exec(sw)?.[1] ?? "";
  const files = [...list.matchAll(/"([^"]+)"/g)].map((x) => x[1]).concat(offline);
  assert.ok(files.length >= 2);
  for (const f of files) assert.ok(existsSync(join(root, "public", f)), `${f} is missing`);
});

test("service worker stores only the offline page, icons, hashed assets, runtime settings and the two listed page shells", () => {
  // Every write to a cache, and what each one is allowed to hold.
  assert.equal([...sw.matchAll(/\.put\(/g)].length, 3);
  assert.match(sw, /const OFFLINE_PAGES = \["\/my-work", "\/dashboard"\];/);
  assert.match(sw, /const keep = OFFLINE_PAGES\.includes\(url\.pathname\) && !url\.search;/);
  assert.match(sw, /\/_next\/static\//);
  // Other origins (the API, the identity provider) and anything but GET are never touched.
  assert.match(sw, /url\.origin !== self\.location\.origin\) return/);
  assert.match(sw, /req\.method !== "GET"\) return/);
  // Pages go to the network first; the saved copy is only a fallback.
  assert.match(sw, /fetch\(req\)\.then/);
  assert.doesNotMatch(sw, /localStorage|Authorization|\/api\//);
});

test("the built pages the worker may keep carry no personal data", { skip: !existsSync(join(root, ".next", "server", "app", "my-work.html")) }, () => {
  for (const page of ["my-work", "dashboard"]) {
    const html = readFileSync(join(root, ".next", "server", "app", `${page}.html`), "utf8");
    assert.doesNotMatch(html, /edunexus\.token|Bearer |eyJ[A-Za-z0-9_-]{10,}|person_id|tenant_id/i, `${page}.html must be a plain shell`);
  }
});

test("the offline page is self-contained (no scripts or assets that would also be offline)", () => {
  const html = readFileSync(join(root, "public", "offline.html"), "utf8");
  assert.doesNotMatch(html, /<script\s+src=|<link\s+[^>]*href=|<img\s/i);
  assert.match(html, /lang="ar"/);
});
