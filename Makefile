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

.PHONY: build app run test clean

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
	codesign --force --sign - "$(APP_BUNDLE)"

run: app
	open "$(APP_BUNDLE)"

test:
	swift test

clean:
	rm -rf .build $(BUILD_DIR)
