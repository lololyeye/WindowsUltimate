import type { APIRoute } from "astro";
import { href } from "../lib/site";

// On a project site (lololyeye.github.io/WindowsUltimate/) crawlers read robots.txt at the domain root,
// so this file only takes effect once the site has its own domain; the sitemap is also linked from every page.
export const GET: APIRoute = ({ site }) =>
  new Response(`User-agent: *\nAllow: /\n\nSitemap: ${new URL(href("sitemap.xml"), site).href}\n`, {
    headers: { "Content-Type": "text/plain; charset=utf-8" },
  });
