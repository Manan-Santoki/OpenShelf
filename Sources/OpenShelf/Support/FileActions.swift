import AppKit

/// File operations shared by the Actions wheel and shelf menus.
@MainActor
enum FileActions {
    static func open(_ urls: [URL]) {
        for url in urls {
            NSWorkspace.shared.open(url)
        }
    }

    static func reveal(_ urls: [URL]) {
        NSWorkspace.shared.activateFileViewerSelecting(urls)
    }

    /// Puts the files themselves on the clipboard, so pasting in Finder copies them.
    static func copyFiles(_ urls: [URL]) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects(urls.map { $0 as NSURL })
    }

    static func copyPaths(_ urls: [URL]) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(urls.map(\.path).joined(separator: "\n"), forType: .string)
    }

    static func airDrop(_ urls: [URL]) {
        guard let service = NSSharingService(named: .sendViaAirDrop), service.canPerform(withItems: urls) else {
            NSSound.beep()
            return
        }
        service.perform(withItems: urls)
    }

    static func openInTerminal(_ urls: [URL]) {
        guard let terminal = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Terminal") else { return }
        let folders = Set(urls.map { url in
            let isDirectory = (try? url.resourceValues(forKeys: [.isDirectoryKey]))?.isDirectory == true
            return isDirectory ? url : url.deletingLastPathComponent()
        })
        NSWorkspace.shared.open(Array(folders), withApplicationAt: terminal, configuration: NSWorkspace.OpenConfiguration())
    }

    /// Asks for a destination folder, then moves the files into it. Name clashes
    /// are reported rather than overwritten. Returns where files went and how many
    /// moved, or nil when cancelled.
    @discardableResult
    static func moveWithPrompt(_ urls: [URL]) -> (destination: URL, movedCount: Int)? {
        let panel = NSOpenPanel()
        panel.title = urls.count == 1 ? "Move “\(urls[0].lastPathComponent)” to…" : "Move \(urls.count) Files to…"
        panel.prompt = "Move"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.directoryURL = urls.first?.deletingLastPathComponent()
        NSApp.activate()
        guard panel.runModal() == .OK, let destination = panel.url else { return nil }

        var failures: [String] = []
        for url in urls {
            do {
                try FileManager.default.moveItem(at: url, to: destination.appendingPathComponent(url.lastPathComponent))
            } catch {
                failures.append("\(url.lastPathComponent): \(error.localizedDescription)")
            }
        }
        if !failures.isEmpty {
            let alert = NSAlert()
            alert.messageText = failures.count == 1 ? "A file couldn’t be moved" : "\(failures.count) files couldn’t be moved"
            alert.informativeText = failures.joined(separator: "\n")
            alert.runModal()
        }
        return (destination, urls.count - failures.count)
    }

    /// Total size of the regular files among `urls`, or nil if it includes a folder.
    static func totalFileSize(of urls: [URL]) -> Int64? {
        var total: Int64 = 0
        for url in urls {
            let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .fileSizeKey])
            if values?.isDirectory == true { return nil }
            total += Int64(values?.fileSize ?? 0)
        }
        return total
    }
}
