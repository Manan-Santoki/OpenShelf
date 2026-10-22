# Static landing page for openshelf.space.
FROM nginx:1.27-alpine
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY public/ /usr/share/nginx/html/
# Runs before nginx starts; adds analytics only when the environment configures it.
COPY --chmod=755 analytics.sh /docker-entrypoint.d/40-analytics.sh
EXPOSE 80
