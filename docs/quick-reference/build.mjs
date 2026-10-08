// Prints the quick reference card to docs/Tempo-Quick-Reference.pdf (two A4 pages, for double-sided printing).
//   node docs/quick-reference/build.mjs
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

const output = path.resolve(here, '..', 'Tempo-Quick-Reference.pdf');
const browser = await playwright.chromium.launch();
const page = await browser.newPage();
await page.goto(pathToFileURL(path.join(here, 'quick-reference.html')).href, { waitUntil: 'networkidle' });
await page.evaluate(() => document.fonts.ready);
await page.pdf({ path: output, format: 'A4', printBackground: true, preferCSSPageSize: true, tagged: true });

// The card is printed double-sided: anything that spills onto a third page has to be cut.
const overflowing = await page.evaluate(() =>
  [...document.querySelectorAll('.page')].filter(p => p.scrollHeight > p.clientHeight + 1).length);
const pages = await page.evaluate(() => document.querySelectorAll('.page').length);
if (pages !== 2 || overflowing > 0) {
  console.warn(`Warning: the card should be exactly two full pages (pages: ${pages}, overflowing: ${overflowing}).`);
  process.exitCode = 1;
}
await browser.close();
console.log(`Wrote ${output}`);
