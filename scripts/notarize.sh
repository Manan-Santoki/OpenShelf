#!/bin/sh
# Notarizes a .zip or .dmg with Apple and staples the ticket.
#   scripts/notarize.sh build/OpenShelf-1.9.0.zip build/OpenShelf.app
#       submits the zip, staples the app (a zip can't hold a ticket)
#   scripts/notarize.sh build/OpenShelf-1.9.0.dmg
#       submits and staples the disk image
#
# Credentials, the first that's set:
#   NOTARY_KEY_PATH, NOTARY_KEY_ID, NOTARY_ISSUER_ID  (App Store Connect API key)
#   NOTARY_APPLE_ID, NOTARY_PASSWORD, NOTARY_TEAM_ID  (Apple ID + app-specific password)
#   NOTARY_PROFILE  (a `xcrun notarytool store-credentials` profile; default "openshelf")
set -eu

file="${1:?usage: notarize.sh <zip|dmg> [path to staple]}"
staple="${2:-$file}"

if [ -n "${NOTARY_KEY_PATH:-}" ]; then
    set -- --key "$NOTARY_KEY_PATH" --key-id "$NOTARY_KEY_ID" --issuer "$NOTARY_ISSUER_ID"
elif [ -n "${NOTARY_APPLE_ID:-}" ]; then
    set -- --apple-id "$NOTARY_APPLE_ID" --password "$NOTARY_PASSWORD" --team-id "$NOTARY_TEAM_ID"
else
    set -- --keychain-profile "${NOTARY_PROFILE:-openshelf}"
fi

out="$(xcrun notarytool submit "$file" "$@" --wait --timeout 30m --output-format json)"
echo "$out"
id="$(echo "$out" | plutil -extract id raw -o - -)"
status="$(echo "$out" | plutil -extract status raw -o - -)"
if [ "$status" != "Accepted" ]; then
    xcrun notarytool log "$id" "$@" || true
    echo "Notarization of $file: $status" >&2
    exit 1
fi

xcrun stapler staple "$staple"
xcrun stapler validate "$staple"
