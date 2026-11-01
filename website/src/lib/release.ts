import fallback from "../data/release-fallback.json";
import { API_LATEST, INSTALLER, type Lang } from "./site";

export interface Release {
  tag: string;
  name: string;
  url: string;
  publishedAt: string;
  asset: { name: string; size: number; digest: string | null; url: string };
}

interface GitHubRelease {
  tag_name: string;
  name: string;
  html_url: string;
  published_at: string;
  assets: { name: string; size: number; digest?: string | null; browser_download_url: string }[];
}

export function fromGitHub(r: GitHubRelease): Release | null {
  const a = r.assets.find((x) => INSTALLER.test(x.name));
  if (!a) return null;
  return {
    tag: r.tag_name,
    name: r.name,
    url: r.html_url,
    publishedAt: r.published_at,
    asset: { name: a.name, size: a.size, digest: a.digest ?? null, url: a.browser_download_url },
  };
}

let cached: Promise<Release> | undefined;

/**
 * Latest release, read from the GitHub API when the site is built. If the API cannot be reached,
 * data/release-fallback.json (a snapshot of a real release) is used, and the page refreshes the
 * values in the browser anyway (components/ReleaseSync.astro).
 */
export function latestRelease(): Promise<Release> {
  cached ??= (async () => {
    try {
      const headers: Record<string, string> = { Accept: "application/vnd.github+json" };
      if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
      const res = await fetch(API_LATEST, { headers });
      if (!res.ok) throw new Error(`GitHub API ${res.status}`);
      const release = fromGitHub(await res.json());
      if (!release) throw new Error("no installer in the latest release");
      return release;
    } catch (e) {
      console.warn(`[release] using the snapshot: ${(e as Error).message}`);
      return fallback as Release;
    }
  })();
  return cached;
}

export const version = (r: Release) => r.tag.replace(/^v/, "");

export function formatSize(bytes: number, lang: Lang = "en"): string {
  const mb = (bytes / 1024 / 1024).toFixed(1);
  return lang === "fr" ? `${mb.replace(".", ",")} Mo` : `${mb} MB`;
}

export function formatDate(iso: string, lang: Lang = "en"): string {
  return new Date(iso).toLocaleDateString(lang === "fr" ? "fr-FR" : "en-GB", { day: "numeric", month: "long", year: "numeric", timeZone: "UTC" });
}

export function sha256(r: Release): string | null {
  return r.asset.digest?.startsWith("sha256:") ? r.asset.digest.slice(7) : null;
}
