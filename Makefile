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

# Sparkle (in-app updates), downloaded at a pinned version and verified.
SPARKLE_VERSION   := 2.10.0
SPARKLE_SHA256    := c2bf58aa8387266ac179357b1415d6f2635f044da8be41042af32425dae6da0c
SPARKLE_DIR       := .build/sparkle
SPARKLE_FRAMEWORK := $(SPARKLE_DIR)/Sparkle.framework
SPARKLE_FLAGS     := -F $(SPARKLE_DIR) -framework Sparkle -Xlinker -rpath -Xlinker @executable_path/../Frameworks

SWIFTC = swiftc -sdk "$(SDK)" -swift-version 6 -parse-as-library -O $(SPARKLE_FLAGS)

# The linker's record of what it linked. App Intents' metadata step reads it
# to see that AppIntents.framework is linked, as it does in Xcode, and skips
# the app otherwise; the framework is named explicitly so it's always listed.
DEPENDENCY_INFO := $(BUILD_DIR)/$(APP_NAME)_dependency_info.dat
LINK_RECORD     = -framework AppIntents -Xlinker -dependency_info -Xlinker $(1)

# Assembles $(APP_BUNDLE) around $(BUILD_DIR)/$(APP_NAME) and ad-hoc signs it.
# The App Intents metadata (Shortcuts and Spotlight actions) needs Xcode; the
# script warns and carries on without it otherwise. SKIP_APPINTENTS=1 skips it.
define bundle_app
	rm -rf "$(APP_BUNDLE)"
	mkdir -p "$(APP_BUNDLE)/Contents/MacOS" "$(APP_BUNDLE)/Contents/Resources" "$(APP_BUNDLE)/Contents/Frameworks"
	cp "$(BUILD_DIR)/$(APP_NAME)" "$(APP_BUNDLE)/Contents/MacOS/$(APP_NAME)"
	cp Support/Info.plist "$(APP_BUNDLE)/Contents/Info.plist"
	cp Support/AppIcon.icns "$(APP_BUNDLE)/Contents/Resources/AppIcon.icns"
	ditto "$(SPARKLE_FRAMEWORK)" "$(APP_BUNDLE)/Contents/Frameworks/Sparkle.framework"
	SDK="$(SDK)" DEPENDENCY_INFO="$(DEPENDENCY_INFO)" EXTRA_SWIFTC_FLAGS="-F $(SPARKLE_DIR)" sh scripts/appintents-metadata.sh "$(APP_BUNDLE)" "$(BUILD_DIR)/appintents" $(SOURCES)
	codesign --force --deep --sign - "$(APP_BUNDLE)"
endef

.PHONY: build app run test clean release dmg sparkle

# Builds with swiftc directly so it works even where SwiftPM manifests cannot.
build: $(BUILD_DIR)/$(APP_NAME)

$(BUILD_DIR)/$(APP_NAME): $(SOURCES) $(SPARKLE_FRAMEWORK)
	mkdir -p $(BUILD_DIR)
	$(SWIFTC) -target $(ARCH)-apple-macosx14.0 $(SOURCES) $(call LINK_RECORD,$(DEPENDENCY_INFO)) -o $@

sparkle: $(SPARKLE_FRAMEWORK)

$(SPARKLE_FRAMEWORK):
	mkdir -p $(SPARKLE_DIR)
	curl -sSL -o "$(SPARKLE_DIR)/Sparkle-$(SPARKLE_VERSION).tar.xz" \
		"https://github.com/sparkle-project/Sparkle/releases/download/$(SPARKLE_VERSION)/Sparkle-$(SPARKLE_VERSION).tar.xz"
	echo "$(SPARKLE_SHA256)  $(SPARKLE_DIR)/Sparkle-$(SPARKLE_VERSION).tar.xz" | shasum -a 256 -c -
	tar -xf "$(SPARKLE_DIR)/Sparkle-$(SPARKLE_VERSION).tar.xz" -C $(SPARKLE_DIR)
	touch $(SPARKLE_FRAMEWORK)

# Wraps the binary in an ad-hoc signed .app bundle with Sparkle embedded.
app: build
	$(bundle_app)

run: app
	open "$(APP_BUNDLE)"

# Universal (Apple silicon + Intel) app, zipped for a GitHub release.
release: $(SPARKLE_FRAMEWORK)
	mkdir -p $(BUILD_DIR)/arm64 $(BUILD_DIR)/x86_64
	for arch in arm64 x86_64; do \
		$(SWIFTC) -target $$arch-apple-macosx14.0 $(SOURCES) $(call LINK_RECORD,$(BUILD_DIR)/$$arch/$(APP_NAME)_dependency_info.dat) -o $(BUILD_DIR)/$$arch/$(APP_NAME) || exit 1; \
	done
	cp $(BUILD_DIR)/$(ARCH)/$(APP_NAME)_dependency_info.dat $(DEPENDENCY_INFO)
	lipo -create -output $(BUILD_DIR)/$(APP_NAME) $(BUILD_DIR)/arm64/$(APP_NAME) $(BUILD_DIR)/x86_64/$(APP_NAME)
	$(bundle_app)
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
		--window-size 600 428 \
		--icon-size 112 \
		--icon "$(APP_NAME).app" 150 190 \
		--hide-extension "$(APP_NAME).app" \
		--app-drop-link 450 190 \
		--no-internet-enable \
		$(DMG_FLAGS) \
		"$(RELEASE_DMG)" "$(APP_BUNDLE)"
	@echo "$(RELEASE_DMG)"

test:
	swift test

clean:
	rm -rf .build $(BUILD_DIR)
