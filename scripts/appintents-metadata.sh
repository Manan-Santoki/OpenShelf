#!/bin/sh
# Adds App Intents metadata (Contents/Resources/Metadata.appintents) to an app
# bundle, so its Shortcuts actions show up in Shortcuts and Spotlight. Xcode
# does this itself; `make` builds with swiftc, so it runs the same two steps:
#   1. compile the sources once more, asking swiftc for the constant values
#      App Intents reads (titles, parameters, phrases) as .swiftconstvalues;
#   2. run Xcode's appintentsmetadataprocessor over those and the binary.
#
#   scripts/appintents-metadata.sh <App.app> <work dir> <source.swift>...
#
# Environment: SDK (required), ARCH (default: this Mac's), DEPLOYMENT_TARGET
# (default 14.0), EXTRA_SWIFTC_FLAGS (e.g. -F for frameworks the sources
# import), SKIP_APPINTENTS=1 to leave the metadata out.
#
# Never fails the build: without Xcode's tool (e.g. with only the Command Line
# Tools) or on any error it prints a warning and the app is built without the
# metadata. Everything else works; the actions just don't appear.
set -u

warn() {
    echo "warning: App Intents metadata not added: $*" >&2
    echo "warning: OpenShelf's Shortcuts actions won't appear in this build. Everything else works." >&2
    exit 0
}

[ "${SKIP_APPINTENTS:-0}" = "1" ] && { echo "Skipping App Intents metadata (SKIP_APPINTENTS=1)."; exit 0; }
[ $# -ge 3 ] || warn "usage: $0 <App.app> <work dir> <source.swift>..."

bundle="$1"
work="$2"
shift 2
sdk="${SDK:?SDK must be set}"
arch="${ARCH:-$(uname -m)}"
deployment="${DEPLOYMENT_TARGET:-14.0}"
module="OpenShelf"
binary="$bundle/Contents/MacOS/$module"
resources="$bundle/Contents/Resources"
triple="$arch-apple-macos$deployment"

processor="$(xcrun --find appintentsmetadataprocessor 2>/dev/null)" || processor=""
[ -n "$processor" ] && [ -x "$processor" ] || warn "Xcode's appintentsmetadataprocessor wasn't found (it comes with Xcode, not the Command Line Tools)"
toolchain="$(cd "$(dirname "$processor")/../.." && pwd)"
bundle_id="$(/usr/libexec/PlistBuddy -c "Print CFBundleIdentifier" "$bundle/Contents/Info.plist" 2>/dev/null)" || warn "couldn't read the bundle identifier"

rm -rf "$work"
mkdir -p "$work" || warn "couldn't create $work"

# The protocols whose conforming types swiftc records constant values for,
# as Xcode lists them. Names the compiler doesn't find are ignored.
cat > "$work/protocols.json" <<'EOF'
[
  "AppIntent",
  "AppEntity",
  "AppEnum",
  "TransientEntity",
  "EntityQuery",
  "DynamicOptionsProvider",
  "AppShortcutsProvider",
  "AppShortcutProviding",
  "AnyResolverProviding",
  "AppIntentsPackage",
  "IntentValueQuery",
  "_IntentValueRepresentable",
  "_AssistantIntentsProvider",
  "_GenerativeFunctionExtractable"
]
EOF

for source in "$@"; do
    case "$source" in
    /*) echo "$source" ;;
    *) echo "$PWD/$source" ;;
    esac
done > "$work/$module.SwiftFileList"
echo "$work/$module.swiftconstvalues" > "$work/$module.SwiftConstValuesFileList"
: > "$work/$module.DependencyMetadataFileList"
: > "$work/$module.DependencyStaticMetadataFileList"

# Step 1. Unoptimised and thrown away afterwards: only the constant values
# are kept. Whole-module mode writes them all to one file.
# shellcheck disable=SC2086
swiftc -sdk "$sdk" -target "$triple" -swift-version 6 -parse-as-library -Onone \
    -module-name "$module" -wmo -c "$@" -o "$work/$module.o" \
    ${EXTRA_SWIFTC_FLAGS:-} \
    -emit-const-values-path "$work/$module.swiftconstvalues" \
    -Xfrontend -const-gather-protocols-file -Xfrontend "$work/protocols.json" \
    || warn "swiftc couldn't extract the constant values"
[ -s "$work/$module.swiftconstvalues" ] || warn "swiftc wrote no constant values"
rm -f "$work/$module.o"

# Step 2. Options differ between Xcode versions, so optional ones are only
# passed when this version's processor lists them.
help="$("$processor" --help 2>&1)" || true
supports() { printf '%s' "$help" | grep -q -- "$1"; }

set -- \
    --toolchain-dir "$toolchain" \
    --module-name "$module" \
    --sdk-root "$sdk" \
    --platform-family macOS \
    --deployment-target "$deployment" \
    --bundle-identifier "$bundle_id" \
    --output "$resources" \
    --target-triple "$triple" \
    --binary-file "$binary" \
    --dependency-file "$work/${module}_dependency_info.dat" \
    --stringsdata-file "$work/ExtractedAppShortcutsMetadata.stringsdata" \
    --source-file-list "$work/$module.SwiftFileList" \
    --metadata-file-list "$work/$module.DependencyMetadataFileList" \
    --swift-const-vals-list "$work/$module.SwiftConstValuesFileList"

if supports --xcode-version; then
    xcode_build="$(xcodebuild -version 2>/dev/null | awk '/Build version/ { print $3 }')"
    [ -n "$xcode_build" ] && set -- "$@" --xcode-version "$xcode_build"
fi
supports --static-metadata-file-list && set -- "$@" --static-metadata-file-list "$work/$module.DependencyStaticMetadataFileList"
supports --compile-time-extraction && set -- "$@" --compile-time-extraction
supports --deployment-aware-processing && set -- "$@" --deployment-aware-processing
supports --validate-assistant-intents && set -- "$@" --validate-assistant-intents
supports --no-app-shortcuts-localization && set -- "$@" --no-app-shortcuts-localization

rm -rf "$resources/Metadata.appintents"
if ! "$processor" "$@"; then
    rm -rf "$resources/Metadata.appintents"
    warn "appintentsmetadataprocessor failed (its output is above)"
fi
if [ ! -d "$resources/Metadata.appintents" ]; then
    warn "appintentsmetadataprocessor ran but wrote no Metadata.appintents"
fi
echo "Added App Intents metadata: $resources/Metadata.appintents"
