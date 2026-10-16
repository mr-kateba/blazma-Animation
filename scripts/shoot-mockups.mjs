// Takes real screenshots of the static mockups (Phase 0 visual check).
// Usage: node scripts/shoot-mockups.mjs <out-dir> [page.html ...]
import { createRequire } from "module";
import path from "path";
import fs from "fs";
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || "/opt/node22/lib/node_modules/playwright");

const out = process.argv[2] || "screenshots";
const pages = process.argv.slice(3).length ? process.argv.slice(3)
  : ["mockup-a-studio.html", "mockup-b-focus.html", "mockup-c-agent.html"];
const dir = path.resolve(path.dirname(new URL(import.meta.url).pathname), "../mockups");
fs.mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
const errors = [];
for (const p of pages) {
  for (const [lang, theme, w, h] of [["en","dark",1440,900],["ar","dark",1440,900],["en","light",1440,900],["ar","dark",390,844]]) {
    const page = await browser.newPage({ viewport: { width: w, height: h } });
    page.on("pageerror", e => errors.push(`${p} ${lang}: ${e.message}`));
    await page.goto(`file://${dir}/${p}?lang=${lang}&theme=${theme}`);
    await page.waitForTimeout(400);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1);
    if (overflow) errors.push(`${p} ${lang} ${w}px: horizontal overflow`);
    await page.screenshot({ path: `${out}/${p.replace(".html","")}-${lang}-${theme}-${w}.png` });
    await page.close();
  }
}
await browser.close();
console.log(errors.length ? "ISSUES:\n" + errors.join("\n") : "OK: no page errors, no horizontal overflow");
