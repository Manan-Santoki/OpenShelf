import AppIntents
import Foundation

/// A failure shown in Shortcuts as written.
struct IntentFailure: LocalizedError, CustomLocalizedStringResourceConvertible {
    let message: String

    init(_ message: String) {
        self.message = message
    }

    var errorDescription: String? { message }

    var localizedStringResource: LocalizedStringResource {
        LocalizedStringResource(stringLiteral: message)
    }
}

/// Turns the files a Shortcuts action was given into places on disk the
/// engines can read and save next to, and its results back into files.
@MainActor
enum IntentFiles {
    /// Where copies of files without a writable folder go while an action
    /// runs. The system clears it out over time.
    nonisolated static var temporaryFolder: URL {
        FileManager.default.temporaryDirectory
            .appendingPathComponent("OpenShelf Shortcuts", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
    }

    /// Where Add to Shelf keeps files that arrive without a place on disk,
    /// so they're still there after the shortcut finishes.
    nonisolated static var keptFolder: URL {
        URL.applicationSupportDirectory
            .appendingPathComponent("OpenShelf", isDirectory: true)
            .appendingPathComponent("From Shortcuts", isDirectory: true)
    }

    /// Runs `body` with `files` as file URLs, keeping access to them open
    /// until it returns.
    /// - Parameters:
    ///   - tool: When given, every file must be a kind the tool works on.
    ///   - needsWritableFolder: Whether results are saved next to the files;
    ///     ones in a folder OpenShelf can't write to are copied first.
    ///   - stagingFolder: Where copies go.
    static func withURLs<T: Sendable>(
        _ files: [IntentFile],
        tool: IntentTool? = nil,
        needsWritableFolder: Bool = true,
        stagingFolder: URL = temporaryFolder,
        _ body: @MainActor ([URL]) async throws -> T
    ) async throws -> T {
        guard !files.isEmpty else { throw IntentFailure("No files were given.") }
        var scoped: [URL] = []
        defer { scoped.forEach { $0.stopAccessingSecurityScopedResource() } }

        var urls: [URL] = []
        for file in files {
            if let url = file.fileURL, url.startAccessingSecurityScopedResource() {
                scoped.append(url)
            }
            let folderIsWritable = file.fileURL.map {
                FileManager.default.isWritableFile(atPath: $0.deletingLastPathComponent().path)
            } ?? false
            let isStaged = IntentRouting.needsStaging(hasFileURL: file.fileURL != nil, folderIsWritable: folderIsWritable || !needsWritableFolder)
            if let url = file.fileURL, !isStaged {
                urls.append(url)
            } else {
                urls.append(try stage(file, in: stagingFolder))
            }
        }

        if let tool {
            for url in urls where !tool.acceptedKinds.contains(FileKind(url: url)) {
                throw IntentFailure(IntentRouting.unacceptedMessage(name: url.lastPathComponent, tool: tool))
            }
        }
        return try await body(urls)
    }

    /// Copies `file` into `folder` under its own name (numbered if taken).
    private static func stage(_ file: IntentFile, in folder: URL) throws -> URL {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let name = file.fileURL?.lastPathComponent ?? file.filename
        let named = URL(fileURLWithPath: name.isEmpty ? "Untitled" : name)
        let pathExtension = named.pathExtension
        let destination = OutputNaming.uniqueURL(
            in: folder,
            baseName: named.deletingPathExtension().lastPathComponent,
            pathExtension: pathExtension.isEmpty ? nil : pathExtension
        )
        if let url = file.fileURL {
            try FileManager.default.copyItem(at: url, to: destination)
        } else {
            try file.data.write(to: destination, options: .withoutOverwriting)
        }
        return destination
    }

    /// Runs `operation` on each file in turn. A file that fails doesn't stop
    /// the rest, but afterwards the action fails, naming it, so an automation
    /// never passes on a partial result silently.
    nonisolated static func each(_ urls: [URL], _ operation: @Sendable (URL) async throws -> [URL]) async throws -> [URL] {
        var results: [URL] = []
        var failures: [(name: String, reason: String)] = []
        for url in urls {
            try Task.checkCancellation()
            do {
                results += try await operation(url)
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                failures.append((url.lastPathComponent, error.localizedDescription))
            }
        }
        if let message = IntentRouting.failureMessage(failures) { throw IntentFailure(message) }
        return results
    }

    static func results(_ urls: [URL]) -> [IntentFile] {
        urls.map { IntentFile(fileURL: $0) }
    }
}

/// The app's shelves, for Add to Shelf. Wired once at launch.
@MainActor
enum IntentShelves {
    static weak var manager: ShelfManager?

    /// The shelves, waiting briefly for launch to finish when an action
    /// started the app.
    static func ready() async -> ShelfManager? {
        for _ in 0..<50 {
            if let manager { return manager }
            try? await Task.sleep(for: .milliseconds(100))
        }
        return manager
    }
}
