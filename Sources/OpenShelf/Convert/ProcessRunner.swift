import Foundation

struct ProcessFailure: LocalizedError {
    let tool: String
    let message: String

    var errorDescription: String? {
        message.isEmpty ? "\(tool) failed." : "\(tool) failed: \(FailureMessage.sanitized(message))"
    }
}

/// An optional tool that was there when the conversion was offered has since
/// been uninstalled (or was never found, if a conversion runs without it).
struct MissingToolError: LocalizedError, Equatable {
    /// How people know the tool: "ffmpeg", "LibreOffice".
    let name: String

    init(name: String) {
        self.name = name
    }

    init(executable: URL) {
        let file = executable.lastPathComponent
        name = file == "soffice" ? "LibreOffice" : file
    }

    var errorDescription: String? {
        "\(name) isn’t installed any more."
    }
}

/// Runs command-line tools without blocking, with cancellation and line-by-line
/// output for progress reporting.
enum ProcessRunner {
    static func run(
        _ executable: URL,
        _ arguments: [String],
        currentDirectory: URL? = nil,
        standardInputFile: URL? = nil,
        standardOutputFile: URL? = nil,
        onOutputLine: (@Sendable (String) -> Void)? = nil
    ) async throws {
        // Launching a missing file fails with a cryptic "file doesn't exist";
        // an optional tool may have been uninstalled since it was detected.
        guard FileManager.default.isExecutableFile(atPath: executable.path) else {
            ExternalTools.refresh()
            throw MissingToolError(executable: executable)
        }
        let box = ProcessBox()
        let process = box.process
        process.executableURL = executable
        process.arguments = arguments
        if let currentDirectory {
            process.currentDirectoryURL = currentDirectory
        }
        if let standardInputFile {
            process.standardInput = try FileHandle(forReadingFrom: standardInputFile)
        }

        let errorPipe = Pipe()
        process.standardError = errorPipe
        errorPipe.fileHandleForReading.readabilityHandler = { handle in
            box.appendError(handle.availableData)
        }

        let outputPipe = Pipe()
        if let standardOutputFile {
            FileManager.default.createFile(atPath: standardOutputFile.path, contents: nil)
            process.standardOutput = try FileHandle(forWritingTo: standardOutputFile)
        } else {
            process.standardOutput = outputPipe
            outputPipe.fileHandleForReading.readabilityHandler = { handle in
                box.appendOutput(handle.availableData, onLine: onOutputLine)
            }
        }

        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                process.terminationHandler = { _ in continuation.resume() }
                do {
                    try process.run()
                } catch {
                    process.terminationHandler = nil
                    continuation.resume(throwing: error)
                }
            }
        } onCancel: {
            box.terminate()
        }

        errorPipe.fileHandleForReading.readabilityHandler = nil
        outputPipe.fileHandleForReading.readabilityHandler = nil
        try Task.checkCancellation()
        guard process.terminationStatus == 0 else {
            throw ProcessFailure(tool: executable.lastPathComponent, message: box.lastErrorLine)
        }
    }
}

/// Owns a `Process` and its buffered output so they can be shared with the
/// cancellation and pipe handlers.
private final class ProcessBox: @unchecked Sendable {
    let process = Process()
    private let lock = NSLock()
    private var errorData = Data()
    private var pendingOutput = ""

    func terminate() {
        lock.withLock {
            if process.isRunning { process.terminate() }
        }
    }

    func appendError(_ data: Data) {
        lock.withLock { errorData.append(data) }
    }

    var lastErrorLine: String {
        lock.withLock {
            String(decoding: errorData, as: UTF8.self)
                .split(whereSeparator: \.isNewline)
                .last
                .map(String.init)?
                .trimmingCharacters(in: .whitespaces) ?? ""
        }
    }

    func appendOutput(_ data: Data, onLine: (@Sendable (String) -> Void)?) {
        guard let onLine, !data.isEmpty else { return }
        let lines: [String] = lock.withLock {
            pendingOutput += String(decoding: data, as: UTF8.self)
            var parts = pendingOutput.components(separatedBy: "\n")
            pendingOutput = parts.removeLast()
            return parts
        }
        lines.forEach(onLine)
    }
}
