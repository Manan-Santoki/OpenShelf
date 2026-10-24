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
        process.environment = ChildEnvironment.minimal()
        if let currentDirectory {
            process.currentDirectoryURL = currentDirectory
        }
        if let standardInputFile {
            process.standardInput = try FileHandle(forReadingFrom: standardInputFile)
        }

        // Each pipe is read to its end on a thread of its own, and the run
        // waits for that after the tool exits: with readability handlers, the
        // last data (afconvert's error, which decides the ffmpeg fallback, or a
        // last output line) could still be on its way when the result was read.
        let readers = DispatchGroup()
        let errorPipe = Pipe()
        process.standardError = errorPipe
        readToEnd(errorPipe.fileHandleForReading, group: readers) { box.appendError($0) }

        let outputPipe = Pipe()
        // Also close parent writers when launch fails, so the reader threads
        // reach EOF instead of retaining the process and its pipes forever.
        defer {
            try? errorPipe.fileHandleForWriting.close()
            try? outputPipe.fileHandleForWriting.close()
        }
        if let standardOutputFile {
            FileManager.default.createFile(atPath: standardOutputFile.path, contents: nil)
            process.standardOutput = try FileHandle(forWritingTo: standardOutputFile)
        } else {
            process.standardOutput = outputPipe
            readToEnd(outputPipe.fileHandleForReading, group: readers) { box.appendOutput($0, onLine: onOutputLine) }
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

        // A process the tool started may keep a pipe open; don't wait on it for long.
        await waitForReaders(readers, upTo: .seconds(2))
        box.finishOutput(onLine: onOutputLine)
        try Task.checkCancellation()
        guard process.terminationStatus == 0 else {
            throw ProcessFailure(tool: executable.lastPathComponent, message: box.lastErrorLine)
        }
    }
}

/// Reads `handle` until its end on a thread of its own, passing each chunk on.
private func readToEnd(_ handle: FileHandle, group: DispatchGroup, chunk: @escaping @Sendable (Data) -> Void) {
    group.enter()
    Thread.detachNewThread {
        while true {
            let data = handle.availableData
            guard !data.isEmpty else { break }
            chunk(data)
        }
        group.leave()
    }
}

/// Waits until every reader has reached the end of its pipe, or `limit` passes.
private func waitForReaders(_ group: DispatchGroup, upTo limit: Duration) async {
    let once = OnceFlag()
    await withCheckedContinuation { (continuation: CheckedContinuation<Void, Never>) in
        group.notify(queue: .global()) { if once.claim() { continuation.resume() } }
        Task {
            try? await Task.sleep(for: limit)
            if once.claim() { continuation.resume() }
        }
    }
}

private final class OnceFlag: @unchecked Sendable {
    private let lock = NSLock()
    private var claimed = false
    func claim() -> Bool { lock.withLock { defer { claimed = true }; return !claimed } }
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

    /// Delivers a last line that had no line break after it.
    func finishOutput(onLine: (@Sendable (String) -> Void)?) {
        guard let onLine else { return }
        let rest: String = lock.withLock {
            defer { pendingOutput = "" }
            return pendingOutput
        }
        if !rest.isEmpty { onLine(rest) }
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
