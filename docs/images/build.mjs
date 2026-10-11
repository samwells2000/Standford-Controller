// Draws the user guide's pictures from diagrams.html: docs/images/<id>.png and <id>.dark.png for each <figure id>.
//   node docs/images/build.mjs
// Needs Playwright with Chromium (npm install -g playwright && npx playwright install chromium).
import { createRequire } from 'node:module';
import { execSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
let playwright;
try {
  playwright = require('playwright');
} catch {
  playwright = require(path.join(execSync('npm root -g').toString().trim(), 'playwright'));
}

const browser = await playwright.chromium.launch();
// Twice the size the guide shows them (720 px wide), so they are sharp on high-resolution screens.
const page = await browser.newPage({ viewport: { width: 800, height: 800 }, deviceScaleFactor: 2 });
await page.goto(pathToFileURL(path.join(here, 'diagrams.html')).href, { waitUntil: 'networkidle' });
await page.evaluate(() => document.fonts.ready);

for (const theme of ['light', 'dark']) {
  await page.evaluate(dark => {
    document.body.classList.toggle('dark', dark);
    // Transparent outside the rounded panel, so the picture sits on the help window's own background.
    document.documentElement.style.background = 'transparent';
    document.body.style.background = 'transparent';
  }, theme === 'dark');
  for (const id of await page.$$eval('figure[id]', figures => figures.map(f => f.id))) {
    const file = path.join(here, theme === 'dark' ? `${id}.dark.png` : `${id}.png`);
    await page.locator(`#${id}`).screenshot({ path: file, omitBackground: true });
    console.log(`Wrote ${path.relative(process.cwd(), file)}`);
  }
}

await browser.close();
