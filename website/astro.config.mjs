import { defineConfig } from "astro/config";

// Published on GitHub Pages at https://lololyeye.github.io/WindowsUltimate/.
// For a custom domain, set SITE_URL (e.g. https://example.com) and BASE_PATH=/ when building:
// .github/workflows/website.yml reads them from the repository variables of the same names.
export default defineConfig({
  site: process.env.SITE_URL || "https://lololyeye.github.io",
  base: process.env.BASE_PATH || "/WindowsUltimate",
  trailingSlash: "ignore",
  build: { format: "directory" },
  compressHTML: true,
  devToolbar: { enabled: false },
});
