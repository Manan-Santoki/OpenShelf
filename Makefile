APP_NAME   := OpenShelf
ARCH       := $(shell uname -m)
BUILD_DIR  := build
APP_BUNDLE := $(BUILD_DIR)/$(APP_NAME).app
SOURCES    := $(shell find Sources -name '*.swift')

# With only the Command Line Tools installed, the macOS 27 SDK's SwiftUI macros
# (e.g. @State) have no compiler plugin, so fall back to the macOS 26 SDK there.
CLT_SDK_26 := /Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk
USES_CLT   := $(findstring CommandLineTools,$(shell xcode-select -p))
SDK        ?= $(if $(and $(USES_CLT),$(wildcard $(CLT_SDK_26))),$(CLT_SDK_26),$(shell xcrun --show-sdk-path))

VERSION    := $(shell /usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Support/Info.plist)
RELEASE_ZIP := $(BUILD_DIR)/$(APP_NAME)-$(VERSION).zip
RELEASE_DMG := $(BUILD_DIR)/$(APP_NAME)-$(VERSION).dmg

.PHONY: build app run test clean release dmg

# Builds with swiftc directly so it works even where SwiftPM manifests cannot.
build: $(BUILD_DIR)/$(APP_NAME)

$(BUILD_DIR)/$(APP_NAME): $(SOURCES)
	mkdir -p $(BUILD_DIR)
	swiftc -sdk "$(SDK)" -swift-version 6 -target $(ARCH)-apple-macosx14.0 \
		-parse-as-library -O $(SOURCES) -o $@

# Wraps the binary in a minimal, ad-hoc signed .app bundle.
app: build
	rm -rf "$(APP_BUNDLE)"
	mkdir -p "$(APP_BUNDLE)/Contents/MacOS" "$(APP_BUNDLE)/Contents/Resources"
	cp "$(BUILD_DIR)/$(APP_NAME)" "$(APP_BUNDLE)/Contents/MacOS/$(APP_NAME)"
	cp Support/Info.plist "$(APP_BUNDLE)/Contents/Info.plist"
	cp Support/AppIcon.icns "$(APP_BUNDLE)/Contents/Resources/AppIcon.icns"
	codesign --force --sign - "$(APP_BUNDLE)"

run: app
	open "$(APP_BUNDLE)"

# Universal (Apple silicon + Intel) app, zipped for a GitHub release.
release:
	mkdir -p $(BUILD_DIR)/arm64 $(BUILD_DIR)/x86_64
	for arch in arm64 x86_64; do \
		swiftc -sdk "$(SDK)" -swift-version 6 -target $$arch-apple-macosx14.0 \
			-parse-as-library -O $(SOURCES) -o $(BUILD_DIR)/$$arch/$(APP_NAME) || exit 1; \
	done
	lipo -create -output $(BUILD_DIR)/$(APP_NAME) $(BUILD_DIR)/arm64/$(APP_NAME) $(BUILD_DIR)/x86_64/$(APP_NAME)
	rm -rf "$(APP_BUNDLE)"
	mkdir -p "$(APP_BUNDLE)/Contents/MacOS" "$(APP_BUNDLE)/Contents/Resources"
	cp "$(BUILD_DIR)/$(APP_NAME)" "$(APP_BUNDLE)/Contents/MacOS/$(APP_NAME)"
	cp Support/Info.plist "$(APP_BUNDLE)/Contents/Info.plist"
	cp Support/AppIcon.icns "$(APP_BUNDLE)/Contents/Resources/AppIcon.icns"
	codesign --force --sign - "$(APP_BUNDLE)"
	rm -f "$(RELEASE_ZIP)"
	ditto -c -k --sequesterRsrc --keepParent "$(APP_BUNDLE)" "$(RELEASE_ZIP)"
	@echo "$(RELEASE_ZIP)"

# Drag-to-Applications installer (needs `brew install create-dmg`). Run after `make release`.
dmg:
	rm -f "$(RELEASE_DMG)"
	create-dmg \
		--volname "$(APP_NAME) $(VERSION)" \
		--volicon Support/AppIcon.icns \
		--background Support/dmg-background.tiff \
		--window-size 600 400 \
		--icon-size 112 \
		--icon "$(APP_NAME).app" 150 190 \
		--hide-extension "$(APP_NAME).app" \
		--app-drop-link 450 190 \
		--no-internet-enable \
		"$(RELEASE_DMG)" "$(APP_BUNDLE)"
	@echo "$(RELEASE_DMG)"

test:
	swift test

clean:
	rm -rf .build $(BUILD_DIR)
