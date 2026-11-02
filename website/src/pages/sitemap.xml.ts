import type { APIRoute } from "astro";
import { pageHref } from "../lib/site";

// Every page in both languages, each listing its other-language version.
const paths = ["", "privacy/", "terms/", "legal/"];

export const GET: APIRoute = ({ site }) => {
  const url = (lang: "en" | "fr", p: string) => new URL(pageHref(lang, p), site).href;
  const entries = paths.flatMap((p) =>
    (["en", "fr"] as const).map(
      (lang) => `  <url>
    <loc>${url(lang, p)}</loc>
    <xhtml:link rel="alternate" hreflang="en" href="${url("en", p)}"/>
    <xhtml:link rel="alternate" hreflang="fr" href="${url("fr", p)}"/>
    <xhtml:link rel="alternate" hreflang="x-default" href="${url("en", p)}"/>
  </url>`,
    ),
  );
  const xml = `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">
${entries.join("\n")}
</urlset>
`;
  return new Response(xml, { headers: { "Content-Type": "application/xml; charset=utf-8" } });
};
