import { test, expect } from '@playwright/test';

// Needs a production build (`npm run build && npm start`): the service worker is not registered in development.
test('installable shell: manifest is served and the offline page appears when the network is gone', async ({ page, context }) => {
  const manifest = await page.request.get('/manifest.webmanifest');
  expect(manifest.ok()).toBeTruthy();
  expect((await manifest.json()).display).toBe('standalone');

  await page.goto('/');
  await page.evaluate(() => navigator.serviceWorker.ready);
  // The worker takes control of this tab on activation; reload so the next navigation goes through it.
  await page.reload();
  await expect.poll(() => page.evaluate(() => !!navigator.serviceWorker.controller)).toBe(true);

  await context.setOffline(true);
  await page.goto('/my-work').catch(() => undefined);
  await expect(page.getByRole('heading', { name: /You are offline/ })).toBeVisible();

  await context.setOffline(false);
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1 })).toContainText('Governance, quality and daily work');
});

test('the service worker stores nothing but the offline page, icons, hashed build assets, runtime settings and two page shells', async ({ page }) => {
  await page.goto('/');
  await page.evaluate(() => navigator.serviceWorker.ready);
  await page.reload();
  await page.goto('/signin');
  const cached = await page.evaluate(async () => {
    const urls: string[] = [];
    for (const name of await caches.keys()) for (const r of await (await caches.open(name)).keys()) urls.push(new URL(r.url).pathname);
    return urls;
  });
  expect(cached.length).toBeGreaterThan(0);
  for (const p of cached) expect(p).toMatch(/^\/(offline\.html|icons\/|_next\/static\/|runtime-config\.js$|my-work$|dashboard$)/);
});
