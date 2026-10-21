import Darwin
import Foundation

/// Runs a program that starts programs of its own (`brew` runs curl, tar,
/// Ruby…) in a new process group, so cancelling stops all of them, the way
/// Ctrl-C does in Terminal. Its output and errors arrive as one stream of
/// lines; it reads nothing.
enum GroupedProcess {
    /// Runs `executable` and returns its exit status (128 + the signal when
    /// one stopped it). Cancelling the task interrupts the group, and
    /// escalates to SIGTERM and then SIGKILL for whatever is still running
    /// `grace` later.
    static func run(
        _ executable: URL,
        _ arguments: [String],
        environment: [String: String],
        grace: Duration = .seconds(3),
        onLine: @escaping @Sendable (String) -> Void
    ) async throws -> Int32 {
        let pipe = Pipe()
        let pid = try spawn(executable, arguments, environment: environment, output: pipe.fileHandleForWriting.fileDescriptor)
        // The child has its own copy; without closing ours, the output never ends.
        try? pipe.fileHandleForWriting.close()

        let run = RunningGroup(pid: pid)
        let lines = LineSplitter(onLine: onLine)
        let status: Int32 = await withTaskCancellationHandler {
            await withCheckedContinuation { (continuation: CheckedContinuation<Int32, Never>) in
                run.onFinish = { continuation.resume(returning: $0) }
                pipe.fileHandleForReading.readabilityHandler = { handle in
                    let data = handle.availableData
                    if data.isEmpty {
                        handle.readabilityHandler = nil
                        lines.finish()
                        run.outputEnded()
                    } else {
                        lines.append(data)
                    }
                }
                Thread.detachNewThread {
                    run.processExited(status: waitForExit(pid))
                }
            }
        } onCancel: {
            Task.detached { await run.stop(grace: grace) }
        }
        pipe.fileHandleForReading.readabilityHandler = nil
        try Task.checkCancellation()
        return status
    }

    /// Starts `executable` as the leader of a new process group, with
    /// standard input from /dev/null, output and errors into `output`, no
    /// other open files, and default signal handling.
    private static func spawn(_ executable: URL, _ arguments: [String], environment: [String: String], output: Int32) throws -> pid_t {
        var attributes: posix_spawnattr_t?
        posix_spawnattr_init(&attributes)
        defer { posix_spawnattr_destroy(&attributes) }
        posix_spawnattr_setflags(&attributes, Int16(POSIX_SPAWN_SETPGROUP | POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_CLOEXEC_DEFAULT))
        posix_spawnattr_setpgroup(&attributes, 0)
        // Signals the app ignores or blocks mustn't carry over, or Cancel
        // couldn't interrupt it.
        var defaults = sigset_t()
        sigemptyset(&defaults)
        for signal in [SIGINT, SIGTERM, SIGHUP, SIGQUIT, SIGPIPE, SIGCHLD] {
            sigaddset(&defaults, signal)
        }
        posix_spawnattr_setsigdefault(&attributes, &defaults)
        var noSignals = sigset_t()
        sigemptyset(&noSignals)
        posix_spawnattr_setsigmask(&attributes, &noSignals)

        var actions: posix_spawn_file_actions_t?
        posix_spawn_file_actions_init(&actions)
        defer { posix_spawn_file_actions_destroy(&actions) }
        posix_spawn_file_actions_addopen(&actions, STDIN_FILENO, "/dev/null", O_RDONLY, 0)
        posix_spawn_file_actions_adddup2(&actions, output, STDOUT_FILENO)
        posix_spawn_file_actions_adddup2(&actions, output, STDERR_FILENO)

        let argv: [UnsafeMutablePointer<CChar>?] = ([executable.path] + arguments).map { strdup($0) } + [nil]
        let envp: [UnsafeMutablePointer<CChar>?] = environment.map { strdup("\($0.key)=\($0.value)") } + [nil]
        defer {
            argv.forEach { free($0) }
            envp.forEach { free($0) }
        }
        var pid: pid_t = 0
        let result = posix_spawn(&pid, executable.path, &actions, &attributes, argv, envp)
        guard result == 0 else {
            throw POSIXError(POSIXErrorCode(rawValue: result) ?? .EPERM)
        }
        return pid
    }

    /// Blocks until `pid` exits, and returns its exit status.
    private static func waitForExit(_ pid: pid_t) -> Int32 {
        var status: Int32 = 0
        while waitpid(pid, &status, 0) == -1 && errno == EINTR {}
        let signal = status & 0x7f
        return signal == 0 ? (status >> 8) & 0xff : 128 + signal
    }
}

/// One running group: its leader's exit and the end of its output, whichever
/// comes last, finish the run.
private final class RunningGroup: @unchecked Sendable {
    let pid: pid_t
    var onFinish: ((Int32) -> Void)?
    private let lock = NSLock()
    private var exitStatus: Int32?
    private var hasOutputEnded = false
    private var hasFinished = false

    init(pid: pid_t) {
        self.pid = pid
    }

    func processExited(status: Int32) {
        lock.withLock { exitStatus = status }
        finishIfDone()
        // Something it started may still hold the output open; don't wait on that.
        Task.detached { [self] in
            try? await Task.sleep(for: .seconds(2))
            lock.withLock { hasOutputEnded = true }
            finishIfDone()
        }
    }

    func outputEnded() {
        lock.withLock { hasOutputEnded = true }
        finishIfDone()
    }

    /// Whether anything is left in the group, the leader or what it started.
    /// A group's ID isn't reused while it has members.
    private var hasMembers: Bool {
        killpg(pid, 0) == 0 || errno == EPERM
    }

    /// Interrupts the group like Ctrl-C, then terminates and finally kills
    /// whatever is still running in it, which can outlive the leader (a
    /// shell's background jobs ignore Ctrl-C).
    func stop(grace: Duration) async {
        for signal in [SIGINT, SIGTERM, SIGKILL] {
            guard killpg(pid, signal) == 0 else { return }
            let deadline = ContinuousClock.now + grace
            while hasMembers, ContinuousClock.now < deadline {
                try? await Task.sleep(for: .milliseconds(50))
            }
        }
    }

    private func finishIfDone() {
        let finish: (callback: (Int32) -> Void, status: Int32)? = lock.withLock {
            guard !hasFinished, hasOutputEnded, let exitStatus, let onFinish else { return nil }
            hasFinished = true
            return (onFinish, exitStatus)
        }
        if let finish { finish.callback(finish.status) }
    }
}

/// Splits output into lines at newlines and at the carriage returns
/// progress bars use, dropping empty ones.
final class LineSplitter: @unchecked Sendable {
    private let onLine: @Sendable (String) -> Void
    private let lock = NSLock()
    private var pending = Data()

    init(onLine: @escaping @Sendable (String) -> Void) {
        self.onLine = onLine
    }

    func append(_ data: Data) {
        let lines: [String] = lock.withLock {
            pending.append(data)
            var lines: [String] = []
            while let end = pending.firstIndex(where: { $0 == 0x0A || $0 == 0x0D }) {
                lines.append(String(decoding: pending[pending.startIndex..<end], as: UTF8.self))
                pending.removeSubrange(pending.startIndex...end)
            }
            return lines
        }
        deliver(lines)
    }

    /// Delivers what's left after the last line break.
    func finish() {
        let rest: String = lock.withLock {
            defer { pending.removeAll() }
            return String(decoding: pending, as: UTF8.self)
        }
        deliver([rest])
    }

    private func deliver(_ lines: [String]) {
        for line in lines where !line.trimmingCharacters(in: .whitespaces).isEmpty {
            onLine(line)
        }
    }
}
