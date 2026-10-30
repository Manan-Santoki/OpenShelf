# Landing page for openshelf.space: public/ served by server.js, which fills
# in the newest release and the download count. Node only, no packages.
FROM node:22-alpine
WORKDIR /site
COPY package.json server.js ./
COPY public/ ./public/
ENV NODE_ENV=production PORT=80
EXPOSE 80
HEALTHCHECK --interval=30s --timeout=3s CMD wget -qO- http://127.0.0.1/healthz >/dev/null || exit 1
CMD ["node", "server.js"]
