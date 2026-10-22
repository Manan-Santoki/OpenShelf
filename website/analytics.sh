#!/bin/sh
# Adds the Rybbit analytics tag to the page when the container starts, from
# the deployment's environment (set in Dokploy), so neither the tracker's
# address nor the site ID lives in the repository. Without both variables
# the page is served with no analytics.
set -eu

page=/usr/share/nginx/html/index.html
script_url="${RYBBIT_SCRIPT_URL:-}"
site_id="${RYBBIT_SITE_ID:-}"

if [ -z "$script_url" ] || [ -z "$site_id" ]; then
    echo "analytics: RYBBIT_SCRIPT_URL or RYBBIT_SITE_ID not set; serving without analytics"
    exit 0
fi

# Only plain values reach the page: an https URL and an alphanumeric ID.
case "$script_url" in
    https://*) ;;
    *) echo "analytics: RYBBIT_SCRIPT_URL must start with https://; serving without analytics"; exit 0 ;;
esac
if printf '%s' "$script_url" | grep -q '[^A-Za-z0-9._~:/?=&%-]' || printf '%s' "$site_id" | grep -q '[^A-Za-z0-9_-]'; then
    echo "analytics: unexpected characters in RYBBIT_SCRIPT_URL or RYBBIT_SITE_ID; serving without analytics"
    exit 0
fi

# The placeholder is replaced once, so a restarted container doesn't add a second tag.
tag="<script src=\"$script_url\" data-site-id=\"$site_id\" defer></script>"
sed -i "s|<!-- analytics -->|$(printf '%s' "$tag" | sed 's/[&|]/\\&/g')|" "$page"
echo "analytics: enabled"
