#!/bin/sh
# Reads CHANGELOG.md.
#   scripts/changelog.sh version        -> prints the newest version, e.g. 1.0.0
#   scripts/changelog.sh notes 1.0.0    -> prints that version's section body
set -eu

changelog="${CHANGELOG:-CHANGELOG.md}"

case "${1:-}" in
version)
    sed -n 's/^## \[\([0-9][0-9.]*\)\].*/\1/p' "$changelog" | head -n 1
    ;;
notes)
    version="${2:?usage: changelog.sh notes <version>}"
    awk -v heading="## [$version]" '
        index($0, heading) == 1 { inside = 1; next }
        inside && /^## \[/ { exit }
        inside { print }
    ' "$changelog" | sed -e '/./,$!d'
    ;;
*)
    echo "usage: changelog.sh version | notes <version>" >&2
    exit 2
    ;;
esac
