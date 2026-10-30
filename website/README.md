# OpenShelf website

The landing page at https://openshelf.space. Plain HTML/CSS/JS in
`public/`, served by `server.js` (Node, no packages; see `Dockerfile`).
Dokploy rebuilds and deploys it on every push to `main` that touches
`website/`.

The server fills the page in from GitHub, so nothing here needs editing for a
release. It reads the releases every ten minutes, keeps the last good answer
when GitHub is unreachable, and fills these markers in `public/index.html`:

| Marker | Becomes |
|---|---|
| `<!--version-->` | the newest version, on each Download button |
| `<!--news-->` | the "Version x is out" link above the headline |
| `<!--downloads-->`, `<!--downloads-sub-->` | the download count in the hero and the Install card |
| `<!-- analytics -->` | the Rybbit tag, when `RYBBIT_SCRIPT_URL` and `RYBBIT_SITE_ID` are set |

The download count is DMG downloads across all releases. GitHub's own total
also counts `appcast.xml` (every update check) and the zip (every in-app
update), so it's several times higher than the number of people installing.

Routes besides the static files:

- `/download` redirects to the newest DMG.
- `/api/release` is the newest version, its page, DMG link and the count, as JSON.
- `/api/badge/downloads` is the count in Shields.io's endpoint format, for the
  README badge.
- `/healthz` answers `ok`.

Environment: `PORT` (80), `GITHUB_REPO` (`Manan-Santoki/OpenShelf`), and
optionally `GITHUB_TOKEN` for a higher GitHub rate limit.

Preview locally: `PORT=8080 node server.js`. Test: `node --test`.
Screenshots in `public/assets` come from the app, rendered with the project's
own sample images.
