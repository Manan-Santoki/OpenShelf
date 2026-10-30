// Serves openshelf.space: the static page in public/, filled in on the server
// with the newest release (version, DMG link, "what's new") and how many
// times the DMG has been downloaded, plus a small API for the README badge.
//
// No dependencies: Node's standard library only.

import { createServer } from "node:http";
import { readFile, stat } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import { createHash } from "node:crypto";
import { extname, join, normalize, sep } from "node:path";
import { fileURLToPath } from "node:url";

const ROOT = fileURLToPath(new URL("./public/", import.meta.url));
const REPO = process.env.GITHUB_REPO || "Manan-Santoki/OpenShelf";
const RELEASES_PAGE = `https://github.com/${REPO}/releases/latest`;
// How long a GitHub answer is reused. Without a token GitHub allows 60
// requests an hour, so this stays well under it.
const REFRESH_MS = 10 * 60 * 1000;
const OLD_HOST = "openshelf.msantoki.com";

const TYPES = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".xml": "application/xml; charset=utf-8",
  ".txt": "text/plain; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".jpg": "image/jpeg",
  ".webp": "image/webp",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
};
const COMPRESSIBLE = new Set([".html", ".css", ".js", ".json", ".xml", ".txt", ".svg"]);

// ---- Releases

/// The newest published release and the DMG's downloads across all releases.
/// GitHub's own total also counts appcast.xml (every update check) and the
/// zip (every in-app update), so only DMGs are counted: people installing.
export function summarize(releases) {
  const published = releases.filter(r => !r.draft && !r.prerelease);
  const latest = published[0];
  if (!latest) return null;
  const dmg = (latest.assets || []).find(a => /\.dmg$/i.test(a.name));
  const downloads = published
    .flatMap(r => r.assets || [])
    .filter(a => /\.dmg$/i.test(a.name))
    .reduce((sum, a) => sum + (a.download_count || 0), 0);
  return {
    version: String(latest.tag_name || "").replace(/^v/, ""),
    url: latest.html_url || RELEASES_PAGE,
    dmg: dmg ? dmg.browser_download_url : "",
    downloads,
  };
}

async function fetchReleases(fetchImpl = fetch) {
  const headers = { Accept: "application/vnd.github+json", "User-Agent": "openshelf.space" };
  if (process.env.GITHUB_TOKEN) headers.Authorization = `Bearer ${process.env.GITHUB_TOKEN}`;
  const all = [];
  let url = `https://api.github.com/repos/${REPO}/releases?per_page=100`;
  // Pages until GitHub stops linking a next one.
  while (url) {
    const response = await fetchImpl(url, { headers, signal: AbortSignal.timeout(10_000) });
    if (!response.ok) throw new Error(`GitHub answered ${response.status}`);
    all.push(...(await response.json()));
    const next = /<([^>]+)>;\s*rel="next"/.exec(response.headers.get("link") || "");
    url = next ? next[1] : null;
  }
  return all;
}

/// Keeps the last good answer and refreshes it in the background, so a slow
/// or failing GitHub never slows the page down.
export function releaseCache({ load = fetchReleases, refreshMs = REFRESH_MS, now = Date.now } = {}) {
  let current = null;
  let fetchedAt = 0;
  let pending = null;
  const listeners = [];

  const refresh = () => {
    if (!pending) {
      pending = load()
        .then(releases => {
          const summary = summarize(releases);
          if (summary) {
            current = { ...summary, updatedAt: new Date(now()).toISOString() };
            listeners.forEach(fn => fn(current));
          }
          fetchedAt = now();
        })
        .catch(error => {
          console.error(`releases: ${error.message}`);
          // Try again in a minute rather than waiting out the full interval.
          fetchedAt = now() - refreshMs + 60_000;
        })
        .finally(() => { pending = null; });
    }
    return pending;
  };

  return {
    get: () => {
      if (now() - fetchedAt >= refreshMs) refresh();
      return current;
    },
    refresh,
    onChange: fn => listeners.push(fn),
  };
}

// ---- Page

const escapeHTML = s => String(s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]);
const formatCount = n => n.toLocaleString("en-US");

/// The analytics tag, from the deployment's environment (set in Dokploy), so
/// neither the tracker's address nor the site ID lives in the repository.
/// Only plain values reach the page: an https URL and an alphanumeric ID.
export function analyticsTag(env = process.env) {
  const url = env.RYBBIT_SCRIPT_URL || "";
  const id = env.RYBBIT_SITE_ID || "";
  if (!url || !id) return "";
  if (!url.startsWith("https://") || /[^A-Za-z0-9._~:/?=&%-]/.test(url) || /[^A-Za-z0-9_-]/.test(id)) {
    console.error("analytics: unexpected RYBBIT_SCRIPT_URL or RYBBIT_SITE_ID; serving without analytics");
    return "";
  }
  return `<script src="${url}" data-site-id="${id}" defer></script>`;
}

/// Fills the page's markers. Without a release (GitHub unreachable since the
/// server started) the markers are just removed: the buttons still work,
/// through /download.
export function renderPage(template, release, analytics = "") {
  const version = release && release.version ? ` <span class="ver">v${escapeHTML(release.version)}</span>` : "";
  const news = release && release.version
    ? `<a class="news" id="news" href="${escapeHTML(release.url)}"><b>New</b><span id="news-text">Version ${escapeHTML(release.version)} is out: see what's new</span><span aria-hidden="true">→</span></a>`
    : "";
  const count = release && release.downloads > 0 ? formatCount(release.downloads) : "";
  return template
    .replaceAll("<!--version-->", version)
    .replace("<!--news-->", news)
    .replace("<!--downloads-->", count ? `<li>${count} downloads</li>` : "")
    .replace("<!--downloads-sub-->", count ? `${count} downloads · ` : "")
    .replace("<!-- analytics -->", analytics);
}

// ---- Serving

const BASE_HEADERS = {
  "X-Content-Type-Options": "nosniff",
  "Referrer-Policy": "strict-origin-when-cross-origin",
};

function cacheControl(path) {
  if (path.startsWith("/assets/")) return "public, max-age=604800";
  if (path === "/robots.txt" || path === "/sitemap.xml") return "public, max-age=3600";
  return "public, max-age=300";
}

/// A body ready to send: its type, an ETag, and a gzipped copy when that helps.
function prepared(body, type, compressible) {
  const buffer = Buffer.isBuffer(body) ? body : Buffer.from(body);
  const gzip = compressible && buffer.length > 1024 ? gzipSync(buffer) : null;
  const etag = `"${createHash("sha1").update(buffer).digest("base64url").slice(0, 20)}"`;
  return { buffer, gzip, type, etag };
}

function send(request, response, status, file, headers = {}) {
  const all = { ...BASE_HEADERS, "Content-Type": file.type, ETag: file.etag, Vary: "Accept-Encoding", ...headers };
  if (status === 200 && request.headers["if-none-match"] === file.etag) {
    response.writeHead(304, all);
    return response.end();
  }
  let body = file.buffer;
  if (file.gzip && /\bgzip\b/.test(request.headers["accept-encoding"] || "")) {
    body = file.gzip;
    all["Content-Encoding"] = "gzip";
  }
  all["Content-Length"] = body.length;
  response.writeHead(status, all);
  response.end(request.method === "HEAD" ? undefined : body);
}

function sendJSON(request, response, value, maxAge = 300) {
  send(request, response, 200, prepared(JSON.stringify(value), TYPES[".json"], false), {
    "Cache-Control": `public, max-age=${maxAge}`,
    "Access-Control-Allow-Origin": "*",
  });
}

function redirect(response, location, status = 302, maxAge = 300) {
  response.writeHead(status, { ...BASE_HEADERS, Location: location, "Cache-Control": `public, max-age=${maxAge}` });
  response.end();
}

export function createSite({ root = ROOT, releases = releaseCache(), env = process.env } = {}) {
  const files = new Map();
  const analytics = analyticsTag(env);
  let page = null;
  let pageFor = undefined;

  async function staticFile(path) {
    if (files.has(path)) return files.get(path);
    const full = normalize(join(root, path));
    if (!full.startsWith(root.endsWith(sep) ? root : root + sep)) return null;
    try {
      if (!(await stat(full)).isFile()) return null;
    } catch {
      return null;
    }
    const ext = extname(full).toLowerCase();
    const file = prepared(await readFile(full), TYPES[ext] || "application/octet-stream", COMPRESSIBLE.has(ext));
    files.set(path, file);
    return file;
  }

  async function home() {
    const release = releases.get();
    if (!page || pageFor !== release) {
      const template = (await readFile(join(root, "index.html"))).toString("utf8");
      page = prepared(renderPage(template, release, analytics), TYPES[".html"], true);
      pageFor = release;
    }
    return page;
  }

  return async function handle(request, response) {
    try {
      const host = (request.headers.host || "").split(":")[0].toLowerCase();
      if (host === OLD_HOST) return redirect(response, `https://openshelf.space${request.url}`, 301, 86400);
      if (request.method !== "GET" && request.method !== "HEAD") {
        response.writeHead(405, { ...BASE_HEADERS, Allow: "GET, HEAD" });
        return response.end();
      }
      let path;
      try {
        path = decodeURIComponent(new URL(request.url, "http://localhost").pathname);
      } catch {
        path = "/";
      }

      switch (path) {
        case "/":
        case "/index.html":
          return send(request, response, 200, await home(), { "Cache-Control": "public, max-age=300" });
        case "/download": {
          // Straight to the newest DMG, so links never need a version in them.
          const release = releases.get();
          return redirect(response, release && release.dmg ? release.dmg : RELEASES_PAGE);
        }
        case "/api/release":
          return sendJSON(request, response, releases.get() || { error: "unavailable" });
        case "/api/badge/downloads": {
          // Shields.io's endpoint badge format, for the README.
          const release = releases.get();
          return sendJSON(request, response, {
            schemaVersion: 1,
            label: "downloads",
            message: release ? formatCount(release.downloads) : "n/a",
            color: "2ea043",
            isError: !release,
          });
        }
        case "/healthz":
          response.writeHead(200, { ...BASE_HEADERS, "Content-Type": "text/plain" });
          return response.end("ok");
      }

      // Unknown paths get a real 404 instead of the home page, so search
      // engines don't index duplicates. 404.html itself is only served as that.
      const file = path === "/404.html" ? null : await staticFile(path.endsWith("/") ? path + "index.html" : path);
      if (file) return send(request, response, 200, file, { "Cache-Control": cacheControl(path) });
      const missing = await staticFile("/404.html");
      return send(request, response, 404, missing, { "Cache-Control": "public, max-age=300" });
    } catch (error) {
      console.error(error);
      if (!response.headersSent) response.writeHead(500, BASE_HEADERS);
      response.end();
    }
  };
}

// Started directly (not imported by the tests).
if (process.argv[1] && fileURLToPath(import.meta.url) === normalize(process.argv[1])) {
  const releases = releaseCache();
  releases.refresh();
  const port = Number(process.env.PORT) || 80;
  const server = createServer(createSite({ releases }));
  server.listen(port, () => console.log(`openshelf.space on port ${port}`));
  const stop = () => server.close(() => process.exit(0));
  process.on("SIGTERM", stop);
  process.on("SIGINT", stop);
}
