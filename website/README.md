# Windows Ultimate website

The official website, published at <https://lololyeye.github.io/WindowsUltimate/>. A static [Astro](https://astro.build) site in English (`/`) and French (`/fr/`), with no tracking, no cookies and no backend.

## Commands

Run from this folder (Node 22):

| Command | What it does |
| --- | --- |
| `npm install` | Install dependencies |
| `npm run dev` | Local server at http://localhost:4321/WindowsUltimate/ |
| `npm run build` | Build the site into `dist/` |
| `npm run preview` | Serve the built site |
| `npm run shots` | Crop raw app captures into `src/assets/shots/` |
| `npm run brand` | Regenerate the favicons and social images in `public/` (Windows only: uses Segoe UI) |

## Release data

The version, date, size and SHA-256 of the installer are read from the GitHub API at build time (`src/lib/release.ts`), with `src/data/release-fallback.json` as a snapshot if the API is unreachable. In the browser, `ReleaseSync` refreshes them from the API so the page never shows an older release. The website workflow also rebuilds after every run of the Release workflow.

## Screenshots

Every product image is a real capture of the app. To regenerate them, build the app, then for each language:

```powershell
./scripts/capture-app.ps1 -Exe <path to WindowsUltimate.App.exe> -Lang en -Out <raw folder>/en
```

The script drives the app through UI Automation, captures each page at 1600 × 1000 and restores your app settings afterwards. Then run `npm run shots -- <raw folder>`. Check every capture for personal information (process names, user names, paths) before committing it.

## Hosting

`.github/workflows/website.yml` builds and deploys to GitHub Pages on every push that touches `website/`. Changes to the website never trigger an app release.

For a custom domain, set these repository variables (Settings → Secrets and variables → Actions → Variables), then add the domain in Settings → Pages:

- `SITE_URL`: `https://example.com`
- `BASE_PATH`: `/`
- `CUSTOM_DOMAIN`: `example.com` (written to `CNAME`)

`robots.txt` only takes effect at the root of a domain, so it applies once the site has its own domain. Until then the sitemap is linked from every page.
