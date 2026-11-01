// Generates the brand files in public/: favicon.svg, favicon.ico (the app's own icon),
// apple-touch-icon.png, and the social previews og.png / og-fr.png (1200 × 630) built around the real
// dashboard screenshot. Text is set in Segoe UI, the app's typeface, so run it on Windows: npm run brand
import sharp from "sharp";
import { copyFileSync, writeFileSync } from "node:fs";

const tile = (size, x = 0, y = 0) => `
  <g transform="translate(${x} ${y}) scale(${size / 32})">
    <defs>
      <linearGradient id="t-fill" x1="0" y1="0" x2="19" y2="32" gradientUnits="userSpaceOnUse">
        <stop offset="0" stop-color="#5a9dff"/><stop offset="0.55" stop-color="#3577f5"/><stop offset="1" stop-color="#2b5fe0"/>
      </linearGradient>
      <linearGradient id="t-edge" x1="0" y1="0" x2="0" y2="32" gradientUnits="userSpaceOnUse">
        <stop offset="0" stop-color="#fff" stop-opacity="0.6"/><stop offset="0.35" stop-color="#fff" stop-opacity="0.15"/><stop offset="1" stop-color="#0b2a80" stop-opacity="0.1"/>
      </linearGradient>
      <radialGradient id="t-sheen" cx="5" cy="0" r="22" gradientUnits="userSpaceOnUse">
        <stop offset="0" stop-color="#fff" stop-opacity="0.32"/><stop offset="1" stop-color="#fff" stop-opacity="0"/>
      </radialGradient>
    </defs>
    <rect x="0.5" y="0.5" width="31" height="31" rx="9" fill="url(#t-fill)"/>
    <rect x="0.5" y="0.5" width="31" height="31" rx="9" fill="url(#t-sheen)"/>
    <rect x="0.5" y="0.5" width="31" height="31" rx="9" fill="none" stroke="url(#t-edge)"/>
    <path d="M7.5 10 L11 22 L16 13.5 L21 22 L24.5 10" fill="none" stroke="#fff" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"/>
  </g>`;

// Favicon: the mark alone.
writeFileSync("public/favicon.svg", `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32">${tile(32)}</svg>\n`);
copyFileSync("../src/WindowsUltimate.App/Assets/app.ico", "public/favicon.ico");

// Apple touch icon: opaque, the mark on the site's graphite with some room around it.
await sharp(Buffer.from(`<svg xmlns="http://www.w3.org/2000/svg" width="180" height="180"><rect width="180" height="180" fill="#0a0f1a"/>${tile(128, 26, 26)}</svg>`))
  .png()
  .toFile("public/apple-touch-icon.png");

// Social previews.
const W = 1200;
const H = 630;
const copy = {
  en: { file: "og.png", a: "Take control", b: "of your PC.", c: "Unlock its potential.", d: "Transparent optimization for Windows 11" },
  fr: { file: "og-fr.png", a: "Reprenez le contrôle", b: "de votre PC.", c: "Libérez son potentiel.", d: "Optimisation transparente pour Windows 11" },
};
const font = "Segoe UI Variable Display, Segoe UI, sans-serif";

for (const [lang, t] of Object.entries(copy)) {
  const size = lang === "fr" ? 47 : 58; // the French line is longer
  const shotW = 860;
  const shot = await sharp(`src/assets/shots/${lang}/dashboard.png`).resize(shotW).png().toBuffer();
  const shotH = Math.round((1000 / 1600) * shotW);
  const sx = 548;
  const sy = 132;
  const rounded = await sharp(shot)
    .composite([{ input: Buffer.from(`<svg width="${shotW}" height="${shotH}"><rect width="${shotW}" height="${shotH}" rx="14" fill="#fff"/></svg>`), blend: "dest-in" }])
    .png()
    .toBuffer();

  const grid = Array.from({ length: 18 }, (_, i) => `<path d="M${i * 72} 0V${H}M0 ${i * 72}H${W}"/>`).join("");
  const ticks = Array.from({ length: 90 }, (_, i) => `<line x1="0" y1="-330" x2="0" y2="${i % 10 === 0 ? -306 : -318}" transform="rotate(${i * 4})"/>`).join("");
  const back = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}">
    <defs>
      <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#08090e"/><stop offset="0.55" stop-color="#0a0f1a"/><stop offset="1" stop-color="#080a10"/></linearGradient>
      <radialGradient id="blue" cx="0.12" cy="-0.05" r="0.75"><stop offset="0" stop-color="#3d78f0" stop-opacity="0.38"/><stop offset="0.5" stop-color="#3d78f0" stop-opacity="0.08"/><stop offset="1" stop-color="#3d78f0" stop-opacity="0"/></radialGradient>
      <radialGradient id="violet" cx="1" cy="1.05" r="0.6"><stop offset="0" stop-color="#7a5cf0" stop-opacity="0.3"/><stop offset="1" stop-color="#7a5cf0" stop-opacity="0"/></radialGradient>
      <radialGradient id="glow" cx="0.5" cy="0.5" r="0.5"><stop offset="0" stop-color="#3d8bff" stop-opacity="0.45"/><stop offset="1" stop-color="#3d8bff" stop-opacity="0"/></radialGradient>
      <radialGradient id="gridmask" cx="0.3" cy="0.2" r="0.7"><stop offset="0" stop-color="#fff" stop-opacity="1"/><stop offset="1" stop-color="#fff" stop-opacity="0"/></radialGradient>
      <mask id="m"><rect width="${W}" height="${H}" fill="url(#gridmask)"/></mask>
      <linearGradient id="title" x1="0" y1="0" x2="0" y2="1"><stop offset="0.1" stop-color="#ffffff"/><stop offset="0.95" stop-color="#c4ccd9"/></linearGradient>
      <linearGradient id="accent" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#7db4ff"/><stop offset="0.55" stop-color="#3d8bff"/><stop offset="1" stop-color="#8a72f0"/></linearGradient>
    </defs>
    <rect width="${W}" height="${H}" fill="url(#bg)"/>
    <g stroke="#a0bee8" stroke-opacity="0.07" mask="url(#m)">${grid}</g>
    <rect width="${W}" height="${H}" fill="url(#blue)"/>
    <rect width="${W}" height="${H}" fill="url(#violet)"/>
    <g transform="translate(${sx + 360} ${sy + 250})" stroke="#a0bee8" stroke-opacity="0.18">${ticks}<circle r="330" fill="none" stroke-opacity="0.1"/></g>
    <ellipse cx="${sx + 380}" cy="${sy + 300}" rx="460" ry="300" fill="url(#glow)"/>
    ${tile(64, 64, 64)}
    <text x="146" y="106" font-family="${font}" font-weight="600" font-size="30" fill="#edf0f5" letter-spacing="-0.5">Windows Ultimate</text>
    <text x="64" y="268" font-family="${font}" font-weight="700" font-size="${size}" fill="url(#title)" letter-spacing="-1.6">${t.a}</text>
    <text x="64" y="334" font-family="${font}" font-weight="700" font-size="${size}" fill="url(#title)" letter-spacing="-1.6">${t.b}</text>
    <text x="64" y="398" font-family="${font}" font-weight="700" font-size="38" fill="url(#accent)" letter-spacing="-0.8">${t.c}</text>
    <circle cx="70" cy="${H - 70}" r="4" fill="#7db4ff"/>
    <text x="86" y="${H - 64}" font-family="Cascadia Mono, Consolas, monospace" font-size="17" fill="#a4aebe">${t.d}</text>
  </svg>`;

  // The window runs off the right and bottom edges: keep only the part inside the card.
  const visible = { left: 0, top: 0, width: Math.min(shotW, W - sx), height: Math.min(shotH, H - sy) };
  const edge = await sharp(Buffer.from(`<svg width="${shotW}" height="${shotH}"><rect x="0.5" y="0.5" width="${shotW - 1}" height="${shotH - 1}" rx="14" fill="none" stroke="#fff" stroke-opacity="0.18"/></svg>`))
    .extract(visible)
    .png()
    .toBuffer();
  await sharp(Buffer.from(back))
    .composite([
      { input: await sharp(rounded).extract(visible).png().toBuffer(), left: sx, top: sy },
      { input: edge, left: sx, top: sy },
    ])
    .png({ compressionLevel: 9 })
    .toFile(`public/${t.file}`);
  console.log(`public/${t.file}`);
}
