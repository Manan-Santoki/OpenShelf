import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { gunzipSync } from "node:zlib";
import { analyticsTag, createSite, releaseCache, renderPage, summarize } from "./server.js";

const releases = [
  { tag_name: "v1.9.0-beta", prerelease: true, assets: [{ name: "OpenShelf-1.9.0-beta.dmg", download_count: 1000 }] },
  {
    tag_name: "v1.8.2", html_url: "https://github.com/x/y/releases/tag/v1.8.2",
    assets: [
      { name: "OpenShelf-1.8.2.dmg", download_count: 40, browser_download_url: "https://github.com/x/y/releases/download/v1.8.2/OpenShelf-1.8.2.dmg" },
      { name: "OpenShelf-1.8.2.zip", download_count: 300 },
      { name: "appcast.xml", download_count: 2000 },
    ],
  },
  { tag_name: "v1.8.1", assets: [{ name: "OpenShelf-1.8.1.dmg", download_count: 1200 }, { name: "appcast.xml", download_count: 900 }] },
];

test("counts DMG downloads only, from published releases", () => {
  assert.deepEqual(summarize(releases), {
    version: "1.8.2",
    url: "https://github.com/x/y/releases/tag/v1.8.2",
    dmg: "https://github.com/x/y/releases/download/v1.8.2/OpenShelf-1.8.2.dmg",
    downloads: 1240,
  });
  assert.equal(summarize([]), null);
});

test("fills the page's markers", () => {
  const template = "<a><!--news--></a><span>Download<!--version--></span><ul><li>x</li><!--downloads--></ul><p><!--downloads-sub-->All</p><!-- analytics -->";
  const html = renderPage(template, summarize(releases), "<script></script>");
  assert.match(html, /Download <span class="ver">v1\.8\.2<\/span>/);
  assert.match(html, /<li>1,240 downloads<\/li>/);
  assert.match(html, /<p>1,240 downloads · All<\/p>/);
  assert.match(html, /Version 1\.8\.2 is out/);
  assert.match(html, /<script><\/script>$/);
  // Without a release the markers simply go away.
  assert.equal(renderPage(template, null), "<a></a><span>Download</span><ul><li>x</li></ul><p>All</p>");
});

test("analytics only with plain values", () => {
  assert.equal(analyticsTag({}), "");
  assert.equal(analyticsTag({ RYBBIT_SCRIPT_URL: "http://a.b/s.js", RYBBIT_SITE_ID: "1" }), "");
  assert.equal(analyticsTag({ RYBBIT_SCRIPT_URL: "https://a.b/s.js", RYBBIT_SITE_ID: "1\"><x" }), "");
  assert.equal(analyticsTag({ RYBBIT_SCRIPT_URL: "https://a.b/s.js", RYBBIT_SITE_ID: "42" }), '<script src="https://a.b/s.js" data-site-id="42" defer></script>');
});

test("keeps the last good answer when GitHub fails", async () => {
  let clock = 0;
  let fail = false;
  const cache = releaseCache({ load: async () => { if (fail) throw new Error("down"); return releases; }, refreshMs: 1000, now: () => clock });
  await cache.refresh();
  assert.equal(cache.get().downloads, 1240);
  fail = true;
  clock = 5000;
  cache.get();
  await cache.refresh();
  assert.equal(cache.get().version, "1.8.2");
});

async function withSite(release, run) {
  const cache = { get: () => release };
  const server = createServer(createSite({ releases: cache, env: {} }));
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    await run(base);
  } finally {
    server.close();
  }
}

test("serves the filled page, the API, the download redirect and 404s", async () => {
  await withSite({ ...summarize(releases), updatedAt: "now" }, async base => {
    const home = await fetch(base + "/");
    assert.equal(home.status, 200);
    const html = await home.text();
    assert.match(html, /1,240 downloads/);
    assert.match(html, /v1\.8\.2/);
    assert.doesNotMatch(html, /<!--version-->|<!--news-->|<!--downloads/);

    const download = await fetch(base + "/download", { redirect: "manual" });
    assert.equal(download.status, 302);
    assert.equal(download.headers.get("location"), "https://github.com/x/y/releases/download/v1.8.2/OpenShelf-1.8.2.dmg");

    const badge = await (await fetch(base + "/api/badge/downloads")).json();
    assert.deepEqual(badge, { schemaVersion: 1, label: "downloads", message: "1,240", color: "2ea043", isError: false });
    const api = await fetch(base + "/api/release");
    assert.equal(api.headers.get("access-control-allow-origin"), "*");
    assert.equal((await api.json()).downloads, 1240);

    const asset = await fetch(base + "/robots.txt");
    assert.equal(asset.status, 200);
    assert.equal(asset.headers.get("cache-control"), "public, max-age=3600");

    for (const path of ["/nope", "/404.html", "/../server.js", "/%2e%2e/server.js"]) {
      const missing = await fetch(base + path);
      assert.equal(missing.status, 404, path);
      assert.doesNotMatch(await missing.text(), /createServer/);
    }

    // fetch won't send another Host, so this one goes by hand.
    const { request } = await import("node:http");
    const old = await new Promise((resolve, reject) => {
      request(base + "/x?y=1", { headers: { host: "openshelf.msantoki.com" } }, res => { res.resume(); resolve(res); }).on("error", reject).end();
    });
    assert.equal(old.statusCode, 301);
    assert.equal(old.headers.location, "https://openshelf.space/x?y=1");
  });
});

test("compresses and revalidates the page", async () => {
  await withSite(summarize(releases), async base => {
    const { request } = await import("node:http");
    const raw = await new Promise((resolve, reject) => {
      request(base + "/", { headers: { "accept-encoding": "gzip" } }, res => {
        const chunks = [];
        res.on("data", c => chunks.push(c));
        res.on("end", () => resolve({ headers: res.headers, body: Buffer.concat(chunks) }));
      }).on("error", reject).end();
    });
    assert.equal(raw.headers["content-encoding"], "gzip");
    assert.match(gunzipSync(raw.body).toString(), /OpenShelf/);
    const again = await fetch(base + "/", { headers: { "if-none-match": raw.headers.etag } });
    assert.equal(again.status, 304);
  });
});

test("works without a release yet", async () => {
  await withSite(null, async base => {
    assert.doesNotMatch(await (await fetch(base + "/")).text(), /<!--version-->|downloads<\/li>/);
    const download = await fetch(base + "/download", { redirect: "manual" });
    assert.equal(download.headers.get("location"), "https://github.com/Manan-Santoki/OpenShelf/releases/latest");
    assert.equal((await (await fetch(base + "/api/badge/downloads")).json()).isError, true);
  });
});
