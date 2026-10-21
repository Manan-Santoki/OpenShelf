# OpenShelf website

The landing page at https://openshelf.msantoki.com. Plain HTML/CSS/JS in
`public/`, served by nginx (`Dockerfile`). Dokploy rebuilds and deploys it on
every push to `main` that touches `website/`.

Preview locally: `python3 -m http.server -d public 8080`.
Screenshots in `public/assets` come from the app, rendered with the project's
own sample images.
