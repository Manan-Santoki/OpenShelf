import AppKit
import SwiftUI
import Testing
@testable import OpenShelf

struct SettingsTabTests {
    @Test func opensOnTheTabChosenLast() {
        #expect(SettingsTab.restored(from: nil) == .general)
        #expect(SettingsTab.restored(from: "extraFormats") == .extraFormats)
        #expect(SettingsTab.restored(from: "updates") == .updates)
        // A tab from a later version, or a damaged value.
        #expect(SettingsTab.restored(from: "advanced") == .general)
    }

    @Test func tabsInToolbarOrder() {
        #expect(SettingsTab.allCases.map(\.title) == ["General", "Wheels", "Shelves", "Extra Formats", "Updates"])
    }

    @Test func everyTabHasASymbol() {
        for tab in SettingsTab.allCases {
            #expect(NSImage(systemSymbolName: tab.symbolName, accessibilityDescription: nil) != nil, "\(tab.symbolName)")
        }
    }

    /// The toolbar's labels fit across the window with room to spare.
    @Test func theToolbarFitsTheWidth() {
        let font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        let labels: CGFloat = SettingsTab.allCases.reduce(0) { total, tab in
            // Each item is at least 52 pt wide, with some padding around its label.
            total + max(52, (tab.title as NSString).size(withAttributes: [.font: font]).width + 20)
        }
        #expect(labels < SettingsTab.width - 60)
    }

    /// Panes that never touch Sparkle or the login item measure to a
    /// sensible height for the window to take.
    @MainActor
    @Test func panesHaveAHeightToFit() {
        for tab in [SettingsTab.wheels, .shelves, .extraFormats] {
            let height = NSHostingView(rootView: tab.pane().fixedSize(horizontal: false, vertical: true)).fittingSize.height
            #expect(height > 80 && height < 700, "\(tab): \(height)")
        }
    }
}

struct SettingsLayoutTests {
    let screen = NSRect(x: 0, y: 25, width: 1440, height: 850)

    @Test func centresAPaneThatFits() {
        let frame = SettingsLayout.frame(contentHeight: 300.2, width: 460, chrome: 80, visible: screen)
        #expect(frame.size == NSSize(width: 460, height: 381))
        let centred = SettingsLayout.centered(frame, in: screen)
        #expect(centred.midX == screen.midX)
        #expect(abs(centred.midY - screen.midY) <= 0.5)
    }

    @Test func aTallPaneScrollsInsteadOfLeavingTheScreen() {
        let frame = SettingsLayout.frame(contentHeight: 2000, width: 460, chrome: 80, visible: screen)
        #expect(frame.height == screen.height - 2 * SettingsLayout.margin)
    }

    @Test func switchingTabsKeepsTheTopEdge() {
        let current = NSRect(x: 300, y: 400, width: 460, height: 300)
        let frame = SettingsLayout.frame(contentHeight: 120, width: 460, chrome: 80, visible: screen, keepingTopOf: current)
        #expect(frame.maxY == current.maxY)
        #expect(frame.minX == current.minX)
        #expect(frame.height == 200)
    }

    @Test func aTallerTabMovesUpRatherThanOffTheBottom() {
        let current = NSRect(x: 300, y: 60, width: 460, height: 300)
        let frame = SettingsLayout.frame(contentHeight: 600, width: 460, chrome: 80, visible: screen, keepingTopOf: current)
        #expect(frame.minY == screen.minY + SettingsLayout.margin)
        #expect(frame.maxY <= screen.maxY)
        #expect(frame.height == 680)
    }
}

/// Renders the panes offscreen to PNGs for a look at their layout. Run with
/// OPENSHELF_RENDER_SETTINGS=<folder>.
struct SettingsRenderTests {
    static let folder = ProcessInfo.processInfo.environment["OPENSHELF_RENDER_SETTINGS"]

    @MainActor
    @Test(.enabled(if: folder != nil))
    func rendersThePanes() throws {
        let folder = URL(fileURLWithPath: try #require(Self.folder))
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for tab in [SettingsTab.general, .wheels, .shelves, .extraFormats] {
            try render(tab.pane(), to: folder.appendingPathComponent(tab.rawValue))
        }

        // Extra Formats while the servers are down, installing, and failing.
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("render-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let installer = ToolInstaller(locations: InstallLocations(tools: root, applications: root))
        installer.refresh()
        let down = InstallFailure(message: SourcesUnavailableError(tool: .ffmpeg, detail: "").localizedDescription, kind: .sourcesUnavailable, detail: "ffmpeg.martin-riedl.de answered with an error (521).")
        installer.failures[.ffmpeg] = down
        installer.progress[.libreOffice] = InstallProgress(step: .installing, fraction: nil, detail: "Installing libreoffice…", source: .homebrew)
        installer.homebrew = URL(fileURLWithPath: "/opt/homebrew/bin/brew")
        try render(ExtraFormatsPane(installer: installer), to: folder.appendingPathComponent("extraFormats-down-brew"))

        installer.homebrew = nil
        installer.progress[.libreOffice] = nil
        installer.failures[.libreOffice] = InstallFailure(message: Homebrew.failureMessage(for: .libreOffice, output: ["sudo: a password is required"]), kind: .homebrew)
        try render(ExtraFormatsPane(installer: installer), to: folder.appendingPathComponent("extraFormats-down-nobrew"))

        installer.failures = [:]
        installer.progress[.ffmpeg] = InstallProgress(step: .downloading, fraction: 0.4, detail: "22 MB of 55 MB", source: .mirror)
        try render(ExtraFormatsPane(installer: installer), to: folder.appendingPathComponent("extraFormats-mirror"))
    }

    @MainActor
    private func render(_ pane: some View, to file: URL) throws {
        let view = NSHostingView(rootView: pane.fixedSize(horizontal: false, vertical: true))
        let size = view.fittingSize
        view.frame = NSRect(origin: .zero, size: size)
        let window = NSWindow(contentRect: view.frame, styleMask: [.borderless], backing: .buffered, defer: false)
        window.contentView = view
        view.layoutSubtreeIfNeeded()
        let bitmap = try #require(view.bitmapImageRepForCachingDisplay(in: view.bounds))
        view.cacheDisplay(in: view.bounds, to: bitmap)
        let png = try #require(bitmap.representation(using: .png, properties: [:]))
        try png.write(to: file.appendingPathExtension("\(Int(size.height)).png"))
    }
}
