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

test("service worker never stores pages or API data, and leaves other origins alone", () => {
  // Only the offline page, icons and hashed build assets are ever put into a cache.
  assert.equal([...sw.matchAll(/cache\.put\(/g)].length, 1);
  assert.match(sw, /\/_next\/static\//);
  assert.match(sw, /url\.origin !== self\.location\.origin\) return/);
  assert.match(sw, /req\.method !== "GET"\) return/);
  // Navigations go to the network first; the offline page is only a fallback.
  assert.match(sw, /fetch\(req\)\.catch\(\(\) => caches\.match\(OFFLINE_URL\)\)/);
  assert.doesNotMatch(sw, /localStorage|Authorization|\/api\//);
});

test("the offline page is self-contained (no scripts or assets that would also be offline)", () => {
  const html = readFileSync(join(root, "public", "offline.html"), "utf8");
  assert.doesNotMatch(html, /<script\s+src=|<link\s+[^>]*href=|<img\s/i);
  assert.match(html, /lang="ar"/);
});
