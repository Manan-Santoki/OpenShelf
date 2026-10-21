import CryptoKit
import Foundation
import Security

/// Why a downloaded tool was rejected. Nothing that fails these checks is
/// ever run; it's deleted.
enum ToolIntegrityError: LocalizedError, Equatable {
    case checksumMismatch(file: String)
    case checksumMissing(file: String)
    case badSignature(file: String)

    var errorDescription: String? {
        switch self {
        case .checksumMismatch(let file): "The download of \(file) was damaged or altered, so it was deleted."
        case .checksumMissing(let file): "The checksum for \(file) couldn’t be found, so it wasn’t installed."
        case .badSignature(let file): "\(file) isn’t signed by its publisher, so it was deleted."
        }
    }
}

/// Checks downloads before anything in them runs: a SHA-256 checksum the
/// publisher lists for the file, and Apple code signing by the publisher's
/// own developer team.
enum ToolIntegrity {
    /// The SHA-256 of a file, as lowercase hex. Reads in chunks; DMGs are large.
    static func sha256(of url: URL) throws -> String {
        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        var hasher = SHA256()
        while let chunk = try handle.read(upToCount: 1 << 20), !chunk.isEmpty {
            hasher.update(data: chunk)
        }
        return hasher.finalize().map { String(format: "%02x", $0) }.joined()
    }

    /// The checksum in a published `.sha256` file: "<64 hex digits>  <name>"
    /// (shasum's format), optionally for one of several files. Nil when
    /// there's none.
    static func publishedChecksum(in text: String, for fileName: String? = nil) -> String? {
        for line in text.split(whereSeparator: \.isNewline) {
            let fields = line.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "*" })
            guard let first = fields.first, first.count == 64, first.allSatisfy(\.isHexDigit) else { continue }
            if let fileName, fields.count > 1, fields.last.map(String.init) != fileName { continue }
            return first.lowercased()
        }
        return nil
    }

    /// Throws unless `file` hashes to `expected`.
    static func verify(_ file: URL, sha256 expected: String, name: String) throws {
        guard try sha256(of: file) == expected.lowercased() else {
            throw ToolIntegrityError.checksumMismatch(file: name)
        }
    }

    /// The code requirement for a Developer ID signature by `teamID`, as
    /// `codesign -d -r-` prints it for the publisher's own builds.
    static func developerIDRequirement(identifier: String, teamID: String) -> String {
        "identifier \"\(identifier)\" and anchor apple generic"
            + " and certificate 1[field.1.2.840.113635.100.6.2.6] exists"
            + " and certificate leaf[field.1.2.840.113635.100.6.1.13] exists"
            + " and certificate leaf[subject.OU] = \"\(teamID)\""
    }

    /// Throws unless the program or app at `url` is intact and signed with a
    /// Developer ID certificate of `teamID`. Apps are checked in full,
    /// including everything nested inside.
    static func verifySignature(of url: URL, identifier: String, teamID: String, name: String) throws {
        var code: SecStaticCode?
        var requirement: SecRequirement?
        guard SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code,
              SecRequirementCreateWithString(developerIDRequirement(identifier: identifier, teamID: teamID) as CFString, [], &requirement) == errSecSuccess,
              let requirement
        else {
            throw ToolIntegrityError.badSignature(file: name)
        }
        let flags = SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate | kSecCSCheckNestedCode)
        guard SecStaticCodeCheckValidityWithErrors(code, flags, requirement, nil) == errSecSuccess else {
            throw ToolIntegrityError.badSignature(file: name)
        }
    }
}
