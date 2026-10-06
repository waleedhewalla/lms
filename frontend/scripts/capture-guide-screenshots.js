const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

(async () => {
  const browser = await chromium.launch({ headless: true });
  const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();

  const outDir = path.join(__dirname, '../../docs/assets');
  if (!fs.existsSync(outDir)) fs.mkdirSync(outDir, { recursive: true });

  // Mock all API calls
  await page.route('**/api/auth/dev-token', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ token: 'dev-mock-token-xyz-1234567890' })
    });
  });

  await page.route('**/api/roles*', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: '1', code: 'ADMIN', name: 'Administrator' },
        { id: '2', code: 'MANAGER', name: 'Manager' },
        { id: '3', code: 'VIEWER', name: 'Read-only Viewer' }
      ])
    });
  });

  await page.route('**/api/roles/assignments*', route => {
    route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([
        { id: '1', personId: 'user-bob', roleId: 'ADMIN' },
        { id: '2', personId: 'user-alice', roleId: 'MANAGER' }
      ])
    });
  });

  await page.route('**/api/roles/assign', route => route.fulfill({ status: 200 }));
  await page.route('**/api/roles/revoke', route => route.fulfill({ status: 200 }));

  // 1. Dashboard Overview
  await page.goto('http://localhost:3020');
  await page.waitForTimeout(1000); // Wait for animations
  await page.screenshot({ path: path.join(outDir, 'dashboard.png') });

  // 2. Roles Management Page
  await page.goto('http://localhost:3020/roles');
  await page.waitForTimeout(1000);
  
  // Fill in Tenant ID and Mint Token to show connected state
  await page.fill('input[placeholder*="b17a1593"]', 'mock-tenant-id');
  await page.click('button:has-text("Mint Dev Token")');
  await page.waitForTimeout(500); // Wait for mock connection state

  // Refresh Data
  await page.click('button:has-text("Refresh Data")');
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(outDir, 'roles-management.png') });

  // 3. Token Connection Highlight
  // We will clip just the ConnectionBar for a specific screenshot
  const connectionBar = await page.locator('.glass-card').first();
  await connectionBar.screenshot({ path: path.join(outDir, 'mint-token.png') });

  await browser.close();
  console.log('Screenshots captured successfully!');
})();
