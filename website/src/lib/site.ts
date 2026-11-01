// Every external link on the site comes from here.
export const REPO = "lololyeye/WindowsUltimate";
export const GITHUB_URL = `https://github.com/${REPO}`;
export const RELEASES_URL = `${GITHUB_URL}/releases`;
export const ISSUES_URL = `${GITHUB_URL}/issues`;
export const README_URL = `${GITHUB_URL}#readme`;
export const API_LATEST = `https://api.github.com/repos/${REPO}/releases/latest`;
/** The other app by the same developer. */
export const AOZE_URL = "https://aoze.fr";
/** The installer the release workflow publishes: WindowsUltimate-<version>-setup.exe. */
export const INSTALLER = /^WindowsUltimate-.+-setup\.exe$/i;

/** Prefixes a site path with the site's base ("/WindowsUltimate/" on GitHub Pages). */
export function href(path = ""): string {
  return import.meta.env.BASE_URL.replace(/\/$/, "") + "/" + path.replace(/^\//, "");
}

export type Lang = "en" | "fr";

/** A page in a given language: English at the root, French under /fr/. */
export function pageHref(lang: Lang, path = ""): string {
  return href((lang === "fr" ? "fr/" : "") + path.replace(/^\//, ""));
}
