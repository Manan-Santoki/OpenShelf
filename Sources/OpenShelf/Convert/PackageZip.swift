import Foundation

/// Builds zip-based document packages (EPUB, PPTX, XLSX) from a folder of
/// parts, using the system's `zip`.
enum PackageZip {
    /// A fresh temporary folder to assemble a package in.
    static func makeWorkspace(_ prefix: String) throws -> URL {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("\(prefix)-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
        return url
    }

    /// Writes `contents` to `path` inside `root`, creating folders as needed.
    static func write(_ contents: String, to path: String, in root: URL) throws {
        try write(Data(contents.utf8), to: path, in: root)
    }

    static func write(_ data: Data, to path: String, in root: URL) throws {
        let url = root.appendingPathComponent(path)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try data.write(to: url)
    }

    /// Zips the contents of `root` into `url`. Entries in `storedFirst` go first
    /// and uncompressed, as EPUB requires for its `mimetype` file.
    static func zip(_ root: URL, to url: URL, storedFirst: [String] = []) throws {
        try? FileManager.default.removeItem(at: url)
        if !storedFirst.isEmpty {
            try run(["-X0q", url.path] + storedFirst, in: root, output: url)
        }
        let rest = try FileManager.default.contentsOfDirectory(atPath: root.path)
            .filter { !storedFirst.contains($0) }
            .sorted()
        try run(["-Xr9Dq", url.path] + rest, in: root, output: url)
    }

    private static func run(_ arguments: [String], in directory: URL, output: URL) throws {
        let zip = ChildEnvironment.process(URL(fileURLWithPath: "/usr/bin/zip"), arguments)
        zip.currentDirectoryURL = directory
        // Failures surface as an error below; keep zip's own message out of the log.
        zip.standardOutput = FileHandle.nullDevice
        zip.standardError = FileHandle.nullDevice
        try zip.run()
        zip.waitUntilExit()
        guard zip.terminationStatus == 0 else { throw EditableImageError.unwritable(output) }
    }

    /// Reads one entry of a zip archive (e.g. `xl/workbook.xml`), or nil when
    /// it's missing.
    static func readEntry(_ path: String, from archive: URL) -> Data? {
        let unzip = ChildEnvironment.process(URL(fileURLWithPath: "/usr/bin/unzip"), ["-p", archive.path, path])
        let pipe = Pipe()
        unzip.standardOutput = pipe
        unzip.standardError = FileHandle.nullDevice
        guard (try? unzip.run()) != nil else { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        unzip.waitUntilExit()
        return unzip.terminationStatus == 0 ? data : nil
    }
}

/// Escaping for text placed in XML and XHTML.
extension String {
    var xmlEscaped: String {
        var result = ""
        result.reserveCapacity(count)
        for scalar in unicodeScalars {
            switch scalar {
            case "&": result += "&amp;"
            case "<": result += "&lt;"
            case ">": result += "&gt;"
            case "\"": result += "&quot;"
            // Control characters other than tab and newlines aren't allowed in XML 1.0.
            case "\t", "\n", "\r": result.unicodeScalars.append(scalar)
            case _ where scalar.value < 0x20: continue
            default: result.unicodeScalars.append(scalar)
            }
        }
        return result
    }
}
