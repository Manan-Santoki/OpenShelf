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
        copyText(urls.map(\.path).joined(separator: "\n"))
    }

    static func copyText(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
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
    static func moveWithPrompt(_ urls: [URL]) -> (destination: URL, movedCount: Int, moves: [URL: URL])? {
        let panel = NSOpenPanel()
        panel.title = urls.count == 1 ? "Move “\(urls[0].lastPathComponent)” to…" : "Move \(urls.count) Files to…"
        panel.prompt = "Move"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        panel.directoryURL = urls.first?.deletingLastPathComponent()
        NSApp.activate()
        guard panel.runModal() == .OK, let destination = panel.url else { return nil }

        let result = move(urls, into: destination)
        let failures = result.failures
        if !failures.isEmpty {
            let alert = NSAlert()
            alert.messageText = failures.count == 1 ? "A file couldn’t be moved" : "\(failures.count) files couldn’t be moved"
            alert.informativeText = failures.joined(separator: "\n")
            alert.runModal()
        }
        return (destination, result.moves.count, result.moves)
    }

    /// Reports successful moves so shelves can follow them; failed moves keep
    /// their existing references. Does not replace files already at the target.
    static func move(_ urls: [URL], into destination: URL) -> (moves: [URL: URL], failures: [String]) {
        var moves: [URL: URL] = [:]
        var failures: [String] = []
        for url in urls {
            let target = destination.appendingPathComponent(url.lastPathComponent)
            do {
                try FileManager.default.moveItem(at: url, to: target)
                moves[url.standardizedFileURL] = target.standardizedFileURL
            } catch {
                failures.append("\(url.lastPathComponent): \(error.localizedDescription)")
            }
        }
        return (moves, failures)
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
