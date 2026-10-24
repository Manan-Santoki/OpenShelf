import Foundation

/// The environment every program OpenShelf starts gets: a short allowlist
/// from the app's own, plus whatever a tool needs. An app opened from a
/// terminal inherits that shell's whole environment (tokens, cloud keys,
/// SSH agent), and none of it should reach ffmpeg, calibre or brew.
enum ChildEnvironment {
    /// Passed through when set.
    static let allowed: Set<String> = ["HOME", "TMPDIR", "USER", "LOGNAME", "SHELL", "LANG", "TERM"]

    /// Where tools look for other tools: Homebrew, MacPorts, the system, and
    /// OpenShelf's own copies. Never the inherited PATH.
    static var searchPath: String {
        (["/opt/homebrew/bin", "/opt/homebrew/sbin", "/usr/local/bin", "/opt/local/bin", "/usr/bin", "/bin", "/usr/sbin", "/sbin"]
            + [ManagedTools.directory.path]).joined(separator: ":")
    }

    /// The allowlisted part of `base` (and its LC_* settings), the search
    /// path, then `extra` on top.
    static func minimal(
        from base: [String: String] = ProcessInfo.processInfo.environment,
        extra: [String: String] = [:]
    ) -> [String: String] {
        var environment = base.filter { (allowed.contains($0.key) || $0.key.hasPrefix("LC_")) && !isCredential($0.key) }
        environment["PATH"] = searchPath
        environment.merge(extra) { _, new in new }
        return environment
    }

    /// Names that look like credentials, which never pass, even when a tool's
    /// own settings (HOMEBREW_*) are let through.
    static func isCredential(_ name: String) -> Bool {
        let upper = name.uppercased()
        return ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "_PASS", "KEY", "AUTH", "CREDENTIAL", "SESSION", "COOKIE"].contains { upper.contains($0) }
            || ["AWS_", "GITHUB_", "GH_", "OP_", "SSH_"].contains { upper.hasPrefix($0) }
    }

    /// A `Process` for `executable` with the minimal environment.
    static func process(_ executable: URL, _ arguments: [String]) -> Process {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.environment = minimal()
        return process
    }
}
