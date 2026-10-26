import Foundation

/// The processor family to download builds for.
enum MachineArchitecture: String, Sendable {
    case arm64
    case x86_64

    /// This Mac's processor, even when OpenShelf itself runs under Rosetta.
    static var current: MachineArchitecture {
        var value: Int32 = 0
        var size = MemoryLayout<Int32>.size
        let found = sysctlbyname("hw.optional.arm64", &value, &size, nil, 0) == 0
        return found && value == 1 ? .arm64 : .x86_64
    }
}

/// ffmpeg and ffprobe from Martin Riedl's build server. Its static builds run
/// without Homebrew, come for Apple silicon and Intel, are signed with his
/// Developer ID and notarized, and each zip has a SHA-256 file beside it.
/// (evermeet.cx, which ffmpeg.org links to, is Intel-only and unsigned.)
enum FFmpegSource {
    static let host = "ffmpeg.martin-riedl.de"
    /// The Team ID of "Developer ID Application: Martin Riedl", read from his builds.
    static let teamID = "KU3N25YGLU"
    /// What both zips together come to, for the Install button.
    static let approximateSize = "55 MB"

    /// OpenShelf's copy of the build server's zips, on a fixed GitHub release,
    /// for when the build server is down: `ffmpeg-arm64.zip`,
    /// `ffprobe-amd64.zip`…, each with a shasum-style `.sha256` beside it.
    /// They're the vendor's own signed zips, unmodified, and go through
    /// exactly the same checks, so the mirror is only a way to fetch them:
    /// nothing it serves can run unless the vendor signed it.
    static let mirrorBase = URL(string: "https://github.com/Manan-Santoki/OpenShelf/releases/download/tools-ffmpeg/")!

    /// The latest release build of `tool` ("ffmpeg" or "ffprobe"). It
    /// redirects to a versioned folder, e.g. …/download/macos/arm64/1783011502_8.1.2/ffmpeg.zip.
    static func latestURL(of tool: String, for architecture: MachineArchitecture) -> URL {
        URL(string: "https://\(host)/redirect/latest/macos/\(platform(architecture))/release/\(tool).zip")!
    }

    /// `tool` on the mirror, e.g. …/tools-ffmpeg/ffprobe-arm64.zip.
    static func mirrorURL(of tool: String, for architecture: MachineArchitecture, base: URL = mirrorBase) -> URL {
        base.appendingPathComponent("\(tool)-\(platform(architecture)).zip")
    }

    /// The build server's name for the processor family, which the mirror shares.
    private static func platform(_ architecture: MachineArchitecture) -> String {
        architecture == .arm64 ? "arm64" : "amd64"
    }

    /// Another file of the same build as `download` (the redirect's target),
    /// so ffprobe always matches the ffmpeg just downloaded.
    static func sibling(_ fileName: String, of download: URL) -> URL {
        download.deletingLastPathComponent().appendingPathComponent(fileName)
    }

    /// Where the build server publishes a file's SHA-256.
    static func checksumURL(for download: URL) -> URL {
        download.deletingLastPathComponent().appendingPathComponent(download.lastPathComponent + ".sha256")
    }

    /// The version in a versioned download URL: "1783011502_8.1.2" → "8.1.2".
    static func version(fromDownloadURL url: URL) -> String? {
        let folder = url.deletingLastPathComponent().lastPathComponent
        guard let separator = folder.firstIndex(of: "_") else { return nil }
        let version = folder[folder.index(after: separator)...]
        return version.isEmpty ? nil : String(version)
    }

    /// Only files from the build server itself, over HTTPS, are accepted.
    static func isTrusted(_ url: URL) -> Bool {
        url.scheme == "https" && url.host == host
    }
}

/// LibreOffice from The Document Foundation's own download server. Its index
/// lists the current versions, MirrorBrain publishes each file's SHA-256 next
/// to it (the file itself may come from a mirror), and the app inside the
/// disk image is signed by the Foundation and notarized.
enum LibreOfficeSource {
    static let stableIndex = URL(string: "https://download.documentfoundation.org/libreoffice/stable/")!
    /// The Team ID of "Developer ID Application: The Document Foundation".
    static let teamID = "7P5S3ZLCN7"
    static let bundleIdentifier = "org.libreoffice.script"
    /// The disk image's size (285 MB for 26.8 on Apple silicon), for the Install button.
    static let approximateSize = "300 MB"

    /// The versions in the stable index ("26.8.1/", "26.2.6/", …), newest first.
    static func versions(inIndex html: String) -> [String] {
        let pattern = #"href="(\d+(?:\.\d+)+)/""#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }
        let range = NSRange(html.startIndex..., in: html)
        let found = regex.matches(in: html, range: range).compactMap { match -> String? in
            Range(match.range(at: 1), in: html).map { String(html[$0]) }
        }
        return Array(Set(found)).sorted { $0.compare($1, options: .numeric) == .orderedDescending }
    }

    /// The disk image for `version`, e.g. …/26.8.1/mac/aarch64/LibreOffice_26.8.1_MacOS_aarch64.dmg.
    /// Intel builds sit in "x86_64" but are named "x86-64".
    static func diskImageURL(version: String, architecture: MachineArchitecture) -> URL {
        let folder = architecture == .arm64 ? "aarch64" : "x86_64"
        let suffix = architecture == .arm64 ? "aarch64" : "x86-64"
        return stableIndex
            .appendingPathComponent(version)
            .appendingPathComponent("mac")
            .appendingPathComponent(folder)
            .appendingPathComponent("LibreOffice_\(version)_MacOS_\(suffix).dmg")
    }

    /// The SHA-256 MirrorBrain serves for a file on the main server.
    static func checksumURL(for diskImage: URL) -> URL {
        diskImage.deletingLastPathComponent().appendingPathComponent(diskImage.lastPathComponent + ".sha256")
    }
}

/// Pandoc from its GitHub releases. GitHub lists a SHA-256 digest for every
/// file of a release, and the program inside the zip is signed with John
/// MacFarlane's Developer ID and notarized.
enum PandocSource {
    static let host = "github.com"
    static let latestRelease = URL(string: "https://api.github.com/repos/jgm/pandoc/releases/latest")!
    /// The Team ID of "Developer ID Application: John Macfarlane".
    static let teamID = "5U2WKE6DES"
    /// The Apple silicon zip's size (42 MB for 3.12), for the Install button.
    static let approximateSize = "45 MB"

    /// One release as GitHub's API describes it.
    struct Release: Decodable {
        struct Asset: Decodable {
            var name: String
            var browserDownloadURL: URL
            /// "sha256:<64 hex digits>".
            var digest: String?

            enum CodingKeys: String, CodingKey {
                case name
                case browserDownloadURL = "browser_download_url"
                case digest
            }
        }

        var tagName: String
        var assets: [Asset]

        enum CodingKeys: String, CodingKey {
            case tagName = "tag_name"
            case assets
        }
    }

    /// The zip for this Mac, e.g. "pandoc-3.12.1-arm64-macOS.zip".
    static func zipName(version: String, architecture: MachineArchitecture) -> String {
        "pandoc-\(version)-\(architecture == .arm64 ? "arm64" : "x86_64")-macOS.zip"
    }

    /// The zip for `architecture` in a release listing, with its version and
    /// SHA-256 (nil when GitHub lists none).
    static func download(inRelease json: Data, for architecture: MachineArchitecture) -> (version: String, url: URL, sha256: String?)? {
        guard let release = try? JSONDecoder().decode(Release.self, from: json) else { return nil }
        let version = release.tagName.hasPrefix("v") ? String(release.tagName.dropFirst()) : release.tagName
        let name = zipName(version: version, architecture: architecture)
        guard let asset = release.assets.first(where: { $0.name == name }) else { return nil }
        let sha256 = asset.digest.flatMap { digest -> String? in
            guard digest.hasPrefix("sha256:") else { return nil }
            let hex = digest.dropFirst("sha256:".count)
            return hex.count == 64 && hex.allSatisfy(\.isHexDigit) ? hex.lowercased() : nil
        }
        return (version, asset.browserDownloadURL, sha256)
    }
}
