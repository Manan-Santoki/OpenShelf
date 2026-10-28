#!/bin/sh
# Adds App Intents metadata (Contents/Resources/Metadata.appintents) to an app
# bundle, so its Shortcuts actions show up in Shortcuts and Spotlight. Xcode
# does this itself; `make` builds with swiftc, so it runs the same two steps:
#   1. compile the sources once more, asking swiftc for the constant values
#      App Intents reads (titles, parameters, phrases) as .swiftconstvalues;
#   2. run Xcode's appintentsmetadataprocessor over those, the binary and the
#      linker's dependency info, which is how it learns that AppIntents.framework
#      is linked; without it, it skips the app ("No AppIntents.framework
#      dependency found").
#
#   scripts/appintents-metadata.sh <App.app> <work dir> <source.swift>...
#
# Environment: SDK (required), DEPENDENCY_INFO (the file the app's link wrote
# with `-Xlinker -dependency_info`), ARCH (default: this Mac's),
# DEPLOYMENT_TARGET (default 14.0), EXTRA_SWIFTC_FLAGS (e.g. -F for frameworks
# the sources import), SKIP_APPINTENTS=1 to leave the metadata out.
#
# Never fails the build: without Xcode's tool (e.g. with only the Command Line
# Tools) or on any error it prints a warning and the app is built without the
# metadata. Everything else works; the actions just don't appear.
set -u

# On GitHub Actions these also become annotations on the run.
annotate() {
    [ "${GITHUB_ACTIONS:-}" = "true" ] && echo "::$1 title=App Intents metadata::$2"
    return 0
}

warn() {
    echo "warning: App Intents metadata not added: $*" >&2
    echo "warning: OpenShelf's Shortcuts actions won't appear in this build. Everything else works." >&2
    annotate warning "Not added: $*"
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
linked="${DEPENDENCY_INFO:-}"
[ -f "$binary" ] || warn "the app's executable isn't at $binary"

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

# The processor's dependency file is the linker's record of its inputs: one
# byte per record (0x00 linker version, 0x10 input, 0x11 not found, 0x40
# output), each followed by a NUL-terminated path. Use the app's own, from
# `-Xlinker -dependency_info`. Should it be missing (a binary linked before
# the Makefile asked for one) or not list AppIntents.framework, record the
# framework the binary loads, provided it really does.
dependency="$work/${module}_dependency_info.dat"
appintents_tbd="$sdk/System/Library/Frameworks/AppIntents.framework/AppIntents.tbd"
if ! otool -L "$binary" 2>/dev/null | grep -q '/AppIntents.framework/'; then
    warn "the executable doesn't link AppIntents.framework (otool -L lists no AppIntents)"
fi
if [ -n "$linked" ] && [ -s "$linked" ]; then
    cp "$linked" "$dependency" || warn "couldn't copy $linked"
    if LC_ALL=C grep -a -q 'AppIntents.framework' "$dependency"; then
        echo "App Intents: using the linker's dependency info ($linked), which lists AppIntents.framework."
    else
        printf '\020%s\000' "$appintents_tbd" >> "$dependency"
        echo "App Intents: the linker's dependency info ($linked) doesn't list AppIntents.framework; added it."
    fi
else
    printf '\000ld\000\020%s\000\100%s\000' "$appintents_tbd" "$binary" > "$dependency"
    echo "App Intents: no linker dependency info${linked:+ at $linked}; wrote one listing AppIntents.framework."
fi

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
    --dependency-file "$dependency" \
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
    warn "appintentsmetadataprocessor ran but wrote no Metadata.appintents (see its warnings above)"
fi
contents="$(cd "$resources/Metadata.appintents" && ls | tr '\n' ' ' | sed 's/ $//')"
echo "==> App Intents metadata added: $resources/Metadata.appintents ($contents)"
annotate notice "Added to $(basename "$bundle"): $contents"
