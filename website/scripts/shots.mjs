// Turns raw captures of the app (scripts/capture-app.ps1, 1600 × 1000 window) into the crops the site
// shows. Crops keep private details out: the process list, startup paths, IP/MAC addresses.
// Usage: node scripts/shots.mjs <captures folder with en/ and fr/>   → src/assets/shots/<lang>/*.png
import sharp from "sharp";
import { mkdirSync } from "node:fs";
import { join } from "node:path";

const src = process.argv[2];
if (!src) throw new Error("usage: node scripts/shots.mjs <captures folder>");

const box = (left, top, right, bottom) => ({ left, top, width: right - left, height: bottom - top });
const content = box(282, 120, 1580, 1000); // the page, without the sidebar
const crops = {
  dashboard: null, // whole window
  gaming: content,
  debloat: content,
  storage: content,
  repair: content,
  monitor: box(282, 160, 1568, 540), // the six charts, not the process list
  responsiveness: box(282, 525, 1580, 965), // the settings list with its risk badges
  network: box(282, 120, 942, 442), // connection quality only, not the adapters (addresses)
};

for (const lang of ["en", "fr"]) {
  const out = join("src/assets/shots", lang);
  mkdirSync(out, { recursive: true });
  for (const [name, crop] of Object.entries(crops)) {
    let img = sharp(join(src, lang, `${name}.png`));
    if (crop) img = img.extract(crop);
    await img.png().toFile(join(out, `${name}.png`));
  }
  // History: the summary bar above one session of changes, stacked as on the page.
  const file = join(src, lang, "history.png");
  const top = await sharp(file).extract(box(282, 120, 1580, 212)).png().toBuffer();
  const block = await sharp(file).extract(box(282, 498, 1580, 850)).png().toBuffer();
  await sharp({ create: { width: 1298, height: 92 + 4 + 352, channels: 3, background: "#0b0f17" } })
    .composite([{ input: top, top: 0, left: 0 }, { input: block, top: 96, left: 0 }])
    .png()
    .toFile(join(out, "history.png"));
  console.log(`${lang}: ${Object.keys(crops).length + 1} images`);
}
