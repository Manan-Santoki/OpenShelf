#!/bin/sh
# Signs OpenShelf.app.
#   SIGN_IDENTITY unset or "-"  -> ad-hoc (local builds, forks)
#   SIGN_IDENTITY="Developer ID Application: … (TEAMID)"
#                               -> hardened runtime + secure timestamp, ready
#                                  for notarization. Sparkle's helpers are
#                                  signed inside out, as Sparkle documents;
#                                  --deep would strip the Downloader's
#                                  entitlements.
set -eu

app="${1:?usage: sign.sh <OpenShelf.app>}"
identity="${SIGN_IDENTITY:--}"
entitlements="$(dirname "$0")/../Support/OpenShelf.entitlements"

if [ "$identity" = "-" ]; then
    codesign --force --deep --sign - "$app"
    exit 0
fi

sign() { codesign --force --timestamp --options runtime --sign "$identity" "$@"; }

sparkle="$app/Contents/Frameworks/Sparkle.framework"
sign "$sparkle/Versions/B/XPCServices/Installer.xpc"
sign --preserve-metadata=entitlements "$sparkle/Versions/B/XPCServices/Downloader.xpc"
sign "$sparkle/Versions/B/Autoupdate"
sign "$sparkle/Versions/B/Updater.app"
sign "$sparkle"
sign --entitlements "$entitlements" "$app"

codesign --verify --strict --deep --verbose=2 "$app"
