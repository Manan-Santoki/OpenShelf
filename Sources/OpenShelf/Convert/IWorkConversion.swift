import AppKit

/// Apple's Pages, Numbers and Keynote documents. Each is a package: a zip
/// file, or a folder in older and iCloud-synced copies.
enum IWorkFormat: String, CaseIterable, Hashable, Sendable {
    case pages
    case numbers
    case keynote

    init?(extension ext: String) {
        guard let format = Self.allCases.first(where: { $0.fileExtension == ext.lowercased() }) else { return nil }
        self = format
    }

    /// The format of the file `url` is, or points to.
    init?(url: URL) {
        self.init(extension: url.resolvingSymlinksInPath().pathExtension)
    }

    var fileExtension: String {
        switch self {
        case .pages: "pages"
        case .numbers: "numbers"
        case .keynote: "key"
        }
    }

    /// The app, and how people know the format.
    var appName: String {
        switch self {
        case .pages: "Pages"
        case .numbers: "Numbers"
        case .keynote: "Keynote"
        }
    }

    /// Newest first: current versions ("Pages Creator Studio" and so on)
    /// use `com.apple.Pages`, older ones `com.apple.iWork.Pages`.
    var bundleIdentifiers: [String] {
        switch self {
        case .pages: ["com.apple.Pages", "com.apple.iWork.Pages"]
        case .numbers: ["com.apple.Numbers", "com.apple.iWork.Numbers"]
        case .keynote: ["com.apple.Keynote", "com.apple.iWork.Keynote"]
        }
    }

    /// The names the app is installed under, newest first.
    var appFileNames: [String] {
        ["\(appName) Creator Studio.app", "\(appName).app"]
    }

    /// The copy macOS would open, current or older; nil when none is installed.
    var installedAppURL: URL? {
        bundleIdentifiers.lazy.compactMap { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) }.first
    }

    /// The Convert wheel's wedge for saving in this format.
    var outputFormat: OutputFormat {
        switch self {
        case .pages: .pages
        case .numbers: .numbers
        case .keynote: .key
        }
    }

    /// How numbered images are labelled: "Report Page 2.png", "Deck Slide 2.png".
    var pageLabel: String {
        self == .keynote ? "Slide" : "Page"
    }

    /// What LibreOffice turns these documents into.
    var libreOfficeOutputs: [OutputFormat] {
        switch self {
        case .pages: [.pdf, .docx, .rtf, .txt, .jpg, .png]
        case .numbers: [.pdf, .xlsx, .csv, .ods, .jpg, .png]
        case .keynote: [.pdf, .pptx, .jpg, .png]
        }
    }

    /// Files the app opens and can save as one of its own documents.
    var importableExtensions: Set<String> {
        switch self {
        case .pages: ["docx", "doc", "rtf", "rtfd", "txt", "text"]
        case .numbers: ["xlsx", "xls", "csv", "tsv"]
        case .keynote: ["pptx", "ppt"]
        }
    }

    /// The app that saves a file with this extension in its own format, if any.
    static func importing(extension ext: String) -> IWorkFormat? {
        allCases.first { $0.importableExtensions.contains(ext.lowercased()) }
    }
}

/// The picture of the document Pages, Numbers and Keynote save inside it, for
/// Quick Look: a PDF of every page in iWork '09 files, a JPEG of the first
/// page (or slide, or sheet) in newer ones. It turns these documents into PDFs
/// and images without LibreOffice.
struct IWorkPreview: Equatable, Sendable {
    /// Where the preview is inside the package, e.g. "QuickLook/Preview.pdf".
    let path: String
    let isPDF: Bool

    /// Previews in order of preference; the best one found is used.
    static let candidates: [(path: String, isPDF: Bool)] = [
        ("QuickLook/Preview.pdf", true),
        ("preview.jpg", false),
        ("QuickLook/Thumbnail.jpg", false),
    ]

    /// What a preview alone converts to.
    var outputs: [OutputFormat] {
        isPDF ? [.pdf, .jpg, .png] : [.jpg, .png]
    }

    /// The best preview in the package at `url`, or nil when it has none (or
    /// isn't a package OpenShelf can read).
    static func find(in url: URL) -> IWorkPreview? {
        let url = url.resolvingSymlinksInPath()
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) else { return nil }
        if isDirectory.boolValue {
            return candidates.first { candidate in
                FileManager.default.fileExists(atPath: url.appendingPathComponent(candidate.path).path)
            }.map { IWorkPreview(path: $0.path, isPDF: $0.isPDF) }
        }
        guard let names = ZipDirectory.entryNames(of: url) else { return nil }
        return find(inEntries: names)
    }

    /// The best preview among a zip's entry names. Packages zipped from
    /// Finder have their contents inside a folder named after them.
    static func find(inEntries names: [String]) -> IWorkPreview? {
        for candidate in candidates {
            let match = names.first { name in
                if name == candidate.path { return true }
                guard name.hasSuffix("/" + candidate.path) else { return false }
                let folder = name.dropLast(candidate.path.count + 1)
                return !folder.isEmpty && !folder.contains("/")
            }
            if let match { return IWorkPreview(path: match, isPDF: candidate.isPDF) }
        }
        return nil
    }

    /// The preview's bytes, from inside the package at `url`.
    func data(in url: URL) -> Data? {
        let url = url.resolvingSymlinksInPath()
        var isDirectory: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) else { return nil }
        if isDirectory.boolValue {
            return try? Data(contentsOf: url.appendingPathComponent(path))
        }
        return PackageZip.readEntry(path, from: url)
    }
}

/// Reads the names in a zip file's central directory, without unpacking it.
/// Cheap enough to run while a wheel opens.
enum ZipDirectory {
    /// The end of central directory record: signature, then 18 bytes, then a
    /// comment of up to 65,535 bytes.
    private static let endRecordSize = 22
    private static let maxCommentSize = 65_535

    /// Entry names, or nil when `url` isn't a zip (or is a ZIP64 one, which
    /// iWork documents almost never are).
    static func entryNames(of url: URL) -> [String]? {
        guard let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let size = try? handle.seekToEnd(), size >= UInt64(endRecordSize) else { return nil }
        let tailSize = min(size, UInt64(endRecordSize + maxCommentSize))
        guard (try? handle.seek(toOffset: size - tailSize)) != nil,
              let tail = try? handle.read(upToCount: Int(tailSize)),
              let directory = centralDirectory(inTail: tail, fileSize: size),
              (try? handle.seek(toOffset: directory.offset)) != nil,
              let data = try? handle.read(upToCount: directory.size),
              data.count == directory.size
        else { return nil }
        return entryNames(inCentralDirectory: data, count: directory.count)
    }

    /// Where the central directory is, from the last bytes of the file.
    static func centralDirectory(inTail tail: Data, fileSize: UInt64) -> (offset: UInt64, size: Int, count: Int)? {
        let bytes = [UInt8](tail)
        guard bytes.count >= endRecordSize else { return nil }
        // The record is the last one with its signature (the comment could hold another).
        for start in stride(from: bytes.count - endRecordSize, through: 0, by: -1)
        where bytes[start] == 0x50 && bytes[start + 1] == 0x4B && bytes[start + 2] == 0x05 && bytes[start + 3] == 0x06 {
            let count = Int(uint16(bytes, at: start + 10))
            let size = UInt64(uint32(bytes, at: start + 12))
            let offset = UInt64(uint32(bytes, at: start + 16))
            // 0xFFFF and 0xFFFFFFFF mean the real values are in a ZIP64 record.
            guard count != 0xFFFF, size != 0xFFFF_FFFF, offset != 0xFFFF_FFFF,
                  offset + size <= fileSize
            else { return nil }
            return (offset, Int(size), count)
        }
        return nil
    }

    /// The names of up to `count` central directory file headers in `data`.
    static func entryNames(inCentralDirectory data: Data, count: Int) -> [String] {
        let bytes = [UInt8](data)
        var names: [String] = []
        var position = 0
        let headerSize = 46
        while names.count < count, position + headerSize <= bytes.count,
              bytes[position] == 0x50, bytes[position + 1] == 0x4B, bytes[position + 2] == 0x01, bytes[position + 3] == 0x02 {
            let nameLength = Int(uint16(bytes, at: position + 28))
            let extraLength = Int(uint16(bytes, at: position + 30))
            let commentLength = Int(uint16(bytes, at: position + 32))
            let nameStart = position + headerSize
            guard nameStart + nameLength <= bytes.count else { break }
            names.append(String(decoding: bytes[nameStart..<nameStart + nameLength], as: UTF8.self))
            position = nameStart + nameLength + extraLength + commentLength
        }
        return names
    }

    private static func uint16(_ bytes: [UInt8], at offset: Int) -> UInt16 {
        UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
    }

    private static func uint32(_ bytes: [UInt8], at offset: Int) -> UInt32 {
        UInt32(bytes[offset]) | UInt32(bytes[offset + 1]) << 8 | UInt32(bytes[offset + 2]) << 16 | UInt32(bytes[offset + 3]) << 24
    }
}

/// Converts Pages, Numbers and Keynote documents: through LibreOffice when
/// it's installed (it reads all three), otherwise from the preview inside them.
enum IWorkConversion {
    /// What `url` converts to with `tools`. LibreOffice's list when it's
    /// installed; otherwise what the preview inside the file can make.
    static func formats(for url: URL, format: IWorkFormat, tools: ExternalTools) -> [OutputFormat] {
        if tools.libreOffice != nil { return format.libreOfficeOutputs }
        return IWorkPreview.find(in: url)?.outputs ?? []
    }

    static func convert(
        _ source: URL,
        from iWork: IWorkFormat,
        to format: OutputFormat,
        tools: ExternalTools,
        settings: OutputSettings,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws -> [URL] {
        let preview = IWorkPreview.find(in: source)
        if tools.libreOffice != nil, iWork.libreOfficeOutputs.contains(format) {
            do {
                return try await OfficeConversion.convert(source, to: format, tools: tools, pageLabel: iWork.pageLabel, progress: progress)
            } catch let error where !(error is CancellationError) {
                // LibreOffice reads most, not all, of these; the preview may still do.
                guard let preview, preview.outputs.contains(format) else { throw error }
                return try convert(source, from: iWork, preview: preview, to: format, settings: settings, progress: progress)
            }
        }
        guard let preview, preview.outputs.contains(format) else {
            if tools.libreOffice == nil {
                throw ConversionError.failed(
                    "“\(source.lastPathComponent)” has no preview inside to convert. Install LibreOffice in Settings → Extra Formats to convert \(iWork.appName) files."
                )
            }
            throw ConversionError.unsupported("\(iWork.appName) to \(format.title)")
        }
        return try convert(source, from: iWork, preview: preview, to: format, settings: settings, progress: progress)
    }

    /// Saves the preview as a PDF or image named after `source`: the PDF as it
    /// is, its pages as pictures, or the JPEG preview as JPEG or PNG.
    private static func convert(
        _ source: URL,
        from iWork: IWorkFormat,
        preview: IWorkPreview,
        to format: OutputFormat,
        settings: OutputSettings,
        progress: @escaping @Sendable (Double) -> Void
    ) throws -> [URL] {
        guard let data = preview.data(in: source), !data.isEmpty else {
            throw ConversionError.failed("The preview inside “\(source.lastPathComponent)” couldn’t be read.")
        }
        let workspace = try PackageZip.makeWorkspace("iwork")
        defer { try? FileManager.default.removeItem(at: workspace) }
        let name = source.deletingPathExtension().lastPathComponent
        let extracted = workspace.appendingPathComponent(name + (preview.isPDF ? ".pdf" : ".jpg"))
        try data.write(to: extracted)

        if preview.isPDF {
            guard format != .pdf else {
                let destination = OutputNaming.destination(for: source, pathExtension: "pdf")
                try FileManager.default.copyItem(at: extracted, to: destination)
                return [destination]
            }
            return try PDFConversion.convert(
                extracted, to: format, savingNextTo: source, pageLabel: iWork.pageLabel,
                imageQuality: settings.image.quality, progress: progress
            )
        }
        guard let pathExtension = format.fileExtension, let type = format.imageIOType else {
            throw ConversionError.unsupported("\(iWork.appName) to \(format.title)")
        }
        let destination = OutputNaming.destination(for: source, pathExtension: pathExtension)
        if format == .jpg {
            try FileManager.default.copyItem(at: extracted, to: destination)
        } else {
            try EditableImage.write(EditableImage.load(extracted).cgImage, to: destination, type: type)
        }
        return [destination]
    }
}

/// Automation access to Pages, Numbers or Keynote is turned off, so the save
/// was refused. The Convert wheel says where to turn it back on.
struct AutomationNotAllowed: LocalizedError, Equatable {
    let appName: String

    var errorDescription: String? {
        "Allow OpenShelf to control \(appName) in System Settings → Privacy & Security → Automation, then try again."
    }
}

/// Saves documents as Pages, Numbers or Keynote files by asking the app
/// itself, over Apple Events. Only offered when the app is installed; the
/// first time, macOS asks whether OpenShelf may control it.
enum IWorkExport {
    /// How long the app gets to open and save the document.
    static let timeout: Duration = .seconds(300)
    /// errAEEventNotPermitted: Automation access to the app was denied.
    static let notPermittedCode = -1743
    private static let osascript = URL(fileURLWithPath: "/usr/bin/osascript")

    static func export(_ source: URL, as iWork: IWorkFormat) async throws -> URL {
        let destination = OutputNaming.destination(for: source, pathExtension: iWork.fileExtension)
        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { try await ProcessRunner.run(osascript, arguments(source: source, destination: destination, app: iWork)) }
                group.addTask {
                    try await Task.sleep(for: timeout)
                    throw ConversionError.failed("\(iWork.appName) took too long to save “\(source.lastPathComponent)”.")
                }
                defer { group.cancelAll() }
                try await group.next()
            }
        } catch let failure as ProcessFailure {
            try? FileManager.default.removeItem(at: destination)
            throw self.failure(forScriptError: failure.message, source: source, app: iWork)
        } catch {
            try? FileManager.default.removeItem(at: destination)
            throw error
        }
        guard FileManager.default.fileExists(atPath: destination.path) else {
            throw ConversionError.failed("\(iWork.appName) couldn’t save “\(source.lastPathComponent)”.")
        }
        return destination
    }

    /// osascript's command line: the script, then the paths it's run with,
    /// so no path is ever spliced into the script's text.
    static func arguments(source: URL, destination: URL, app: IWorkFormat) -> [String] {
        ["-e", script(bundleIdentifier: installedBundleIdentifier(of: app)), source.path, destination.path]
    }

    /// The identifier of the copy macOS would open, current or older.
    static func installedBundleIdentifier(of app: IWorkFormat) -> String {
        app.bundleIdentifiers.first { NSWorkspace.shared.urlForApplication(withBundleIdentifier: $0) != nil }
            ?? app.bundleIdentifiers[0]
    }

    /// Opens the file in the app, saves it as one of the app's documents and
    /// closes it, quitting the app again if it wasn't already open.
    static func script(bundleIdentifier: String) -> String {
        """
        on run argv
            set sourceFile to POSIX file (item 1 of argv)
            set targetFile to POSIX file (item 2 of argv)
            set wasRunning to application id "\(bundleIdentifier)" is running
            tell application id "\(bundleIdentifier)"
                with timeout of 300 seconds
                    set theDocument to open sourceFile
                    try
                        save theDocument in targetFile
                    on error errorText number errorNumber
                        close theDocument saving no
                        if not wasRunning then quit
                        error errorText number errorNumber
                    end try
                    close theDocument saving no
                end timeout
                if not wasRunning then quit
            end tell
        end run
        """
    }

    /// What osascript's error output means, as an error to throw.
    static func failure(forScriptError text: String, source: URL, app: IWorkFormat) -> any Error {
        if text.contains("(\(notPermittedCode))") {
            return AutomationNotAllowed(appName: app.appName)
        }
        // -10814: no app with that bundle identifier; -600: it quit or never launched.
        if text.contains("(-10814)") || text.contains("(-600)") {
            return ConversionError.failed("\(app.appName) isn’t installed any more.")
        }
        return ConversionError.failed("\(app.appName) couldn’t save “\(source.lastPathComponent)”: \(FailureMessage.sanitized(text))")
    }
}
