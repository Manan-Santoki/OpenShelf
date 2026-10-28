import AppKit
import Foundation
import Testing
@testable import OpenShelf

/// What the Convert wheel offers with no optional tools against all of them.
struct MissingToolCatalogTests {
    let none = ExternalTools()
    let all = ExternalTools(
        ffmpeg: URL(fileURLWithPath: "/usr/bin/true"),
        cwebp: URL(fileURLWithPath: "/usr/bin/true"),
        ffprobe: nil,
        libreOffice: URL(fileURLWithPath: "/usr/bin/true")
    )
    private func url(_ name: String) -> URL { URL(fileURLWithPath: "/tmp/\(name)") }
    private func formats(_ name: String, _ tools: ExternalTools) -> [OutputFormat] {
        ConversionCatalog.formats(for: [url(name)], tools: tools)
    }

    @Test func images() {
        #expect(formats("a.jpg", none) == [.png, .heic, .tiff, .avif, .bmp, .pdf, .docx, .zip])
        #expect(formats("a.jpg", all) == [.png, .webp, .heic, .tiff, .avif, .bmp, .pdf, .docx, .zip])
    }

    @Test func videoGetsGIFWithoutFFmpeg() {
        #expect(formats("a.mp4", none) == [.mov, .m4a, .gif, .zip])
        #expect(formats("a.mp4", all) == [.mov, .m4a, .gif, .mp3, .mkv, .webm, .avi, .wmv, .zip])
        #expect(formats("a.mov", none).contains(.gif))
    }

    @Test func audio() {
        #expect(formats("a.mp3", none) == [.m4a, .wav, .aiff, .flac, .zip])
        #expect(formats("a.mp3", all) == [.m4a, .wav, .aiff, .flac, .ogg, .opus, .wma, .zip])
        #expect(formats("a.ogg", none) == [.zip])
    }

    @Test func containersMacOSCantOpen() {
        #expect(formats("a.mkv", none) == [.zip])
        #expect(formats("a.mkv", all) == [.mp4, .mov, .m4a, .gif, .mp3, .webm, .avi, .wmv, .zip])
    }

    @Test func slidesAndSpreadsheets() {
        #expect(formats("a.pptx", none) == [.zip])
        #expect(formats("a.pptx", all) == [.pdf, .jpg, .png, .zip])
        #expect(formats("a.xls", none) == [.zip])
        #expect(formats("a.xls", all) == [.xlsx, .csv, .pdf, .ods, .zip])
        #expect(formats("a.csv", none) == [.xlsx, .tsv, .pdf, .zip])
        #expect(formats("a.csv", all) == [.xlsx, .tsv, .pdf, .ods, .zip])
    }

    @Test func foldersAreTheSameEitherWay() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("folder-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(ConversionCatalog.formats(for: [folder], tools: none) == [.zip, .tar, .tgz])
        #expect(ConversionCatalog.formats(for: [folder], tools: all) == [.zip, .tar, .tgz])
    }

    @Test func gifNoLongerNeedsFFmpeg() {
        #expect(!OutputFormat.gif.needsFFmpeg)
        #expect(OutputFormat.mp3.needsFFmpeg)
    }
}

/// When to say a tool is missing instead of showing a ZIP-only wheel.
struct MissingToolAdviceTests {
    let none = ExternalTools()
    let ffmpegOnly = ExternalTools(ffmpeg: URL(fileURLWithPath: "/usr/bin/true"))
    private func urls(_ names: String...) -> [URL] { names.map { URL(fileURLWithPath: "/tmp/\($0)") } }

    private func blocking(_ urls: [URL], _ tools: ExternalTools) -> ExtraTool? {
        MissingToolAdvice.blockingTool(for: urls, formats: ConversionCatalog.formats(for: urls, tools: tools), tools: tools)
    }

    @Test func namesTheToolThatWouldUnlockConversions() {
        for name in ["a.mkv", "a.webm", "a.avi", "a.ogg", "a.wma"] {
            #expect(blocking(urls(name), none) == .ffmpeg, "\(name)")
        }
        for name in ["a.pptx", "a.ppt", "a.odp", "a.xls", "a.ods", "a.key", "a.pages", "a.numbers"] {
            #expect(blocking(urls(name), none) == .libreOffice, "\(name)")
        }
        #expect(blocking(urls("a.pptx"), ffmpegOnly) == .libreOffice)
    }

    @Test func zipWheelsStayWhenNoToolWouldHelp() throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("advice-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        #expect(blocking([folder], none) == nil)
        #expect(blocking(urls("a.xyz123"), none) == nil)
        // An MKV and a deck share no format whatever is installed.
        #expect(blocking(urls("a.mkv", "b.pptx"), none) == nil)
        #expect(blocking(urls("a.mkv"), ffmpegOnly) == nil)
        // Wheels with real conversions are left alone.
        #expect(blocking(urls("a.mp4"), none) == nil)
        #expect(blocking(urls("a.zip"), none) == nil)
    }

    @Test func mixedSelectionsNameOnlyTheBlockedFiles() {
        #expect(blocking(urls("a.mkv", "b.mp4"), none) == .ffmpeg)
        #expect(MissingToolAdvice.toast(for: urls("a.mkv", "b.mp4"), needing: .ffmpeg, tools: none).title == "MKV needs ffmpeg")
    }

    @Test func toastTitles() {
        func title(_ names: String..., tool: ExtraTool) -> String {
            MissingToolAdvice.toast(for: names.map { URL(fileURLWithPath: "/tmp/\($0)") }, needing: tool, tools: none).title
        }
        #expect(title("a.mkv", tool: .ffmpeg) == "MKV needs ffmpeg")
        #expect(title("a.mkv", "b.mkv", tool: .ffmpeg) == "MKV needs ffmpeg")
        #expect(title("a.pptx", tool: .libreOffice) == "PowerPoint needs LibreOffice")
        #expect(title("a.mkv", "b.webm", tool: .ffmpeg) == "MKV and WebM need ffmpeg")
        #expect(title("a.mkv", "b.webm", "c.avi", tool: .ffmpeg) == "These files need ffmpeg")
        #expect(title("a.xls", "b.ods", tool: .libreOffice) == "XLS and ODS need LibreOffice")
        #expect(title("a.pptx", "b.xls", tool: .libreOffice) == "These files need LibreOffice")
        let toast = MissingToolAdvice.toast(for: urls("a.ogg"), needing: .ffmpeg, tools: none)
        #expect(toast.subtitle == "Install it in Settings → Extra Formats")
    }

    @Test func toastLinesFitTheToast() {
        // ConfirmationPill: 12 pt semibold title, 11 pt subtitle capped at 220 pt.
        let toast = MissingToolAdvice.toast(for: urls("a.pptx"), needing: .libreOffice, tools: none)
        let titleWidth = (toast.title as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 12, weight: .semibold)]).width
        let subtitleWidth = ((toast.subtitle ?? "") as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 11)]).width
        #expect(titleWidth <= 240)
        #expect(subtitleWidth <= 220)
        for tool in ExtraTool.allCases {
            let width = (tool.installedConfirmation as NSString).size(withAttributes: [.font: NSFont.systemFont(ofSize: 11)]).width
            #expect(width <= 220, "\(tool)")
        }
    }

    @Test func convertFirstToastSaysWhenFFmpegIsMissing() {
        let toast = FileTool.convertFirstToast(for: urls("a.mkv"), convertKeys: "⇧", tools: none)
        #expect(toast.title == "MKV needs ffmpeg")
        #expect(toast.subtitle == MissingToolAdvice.installHint)
        #expect(FileTool.convertFirstToast(for: urls("a.wma"), convertKeys: "⇧", tools: none).title == "WMA needs ffmpeg")
        #expect(FileTool.convertFirstToast(for: urls("a.mkv"), convertKeys: "⇧", tools: ffmpegOnly).title == "Convert to MP4 first")
    }

    @Test func moreFormatsHintOnlyForMediaMacOSReads() {
        #expect(MissingToolAdvice.moreFormatsHint(for: urls("a.mp4"), tools: none) != nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: urls("a.mp3", "b.mov"), tools: none) != nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: urls("a.mp4"), tools: ffmpegOnly) == nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: urls("a.mkv"), tools: none) == nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: urls("a.jpg"), tools: none) == nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: urls("a.mp4", "b.jpg"), tools: none) == nil)
        #expect(MissingToolAdvice.moreFormatsHint(for: [], tools: none) == nil)
    }

    @Test func assumingAToolNeverRunsAnything() {
        let assumed = ExtraTool.ffmpeg.assumedInstalled(in: none)
        #expect(assumed.ffmpeg != nil)
        #expect(assumed.ffprobe == nil)
        #expect(ExtraTool.libreOffice.assumedInstalled(in: none).libreOffice != nil)
    }
}

/// Where tools are found, and in which order.
struct ToolDetectionTests {
    private func makeFolder() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("detect-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    private func makeExecutable(_ name: String, in folder: URL) throws -> URL {
        let url = folder.appendingPathComponent(name)
        try Data("#!/bin/sh\n".utf8).write(to: url)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: url.path)
        return url
    }

    @Test func homebrewComesBeforeOpenShelfsOwnCopy() throws {
        let homebrew = try makeFolder()
        let managed = try makeFolder()
        defer {
            try? FileManager.default.removeItem(at: homebrew)
            try? FileManager.default.removeItem(at: managed)
        }
        let ownFFmpeg = try makeExecutable("ffmpeg", in: managed)
        let ownFFprobe = try makeExecutable("ffprobe", in: managed)
        let locations = ToolLocations(commandDirectories: [homebrew, managed], libreOfficeApps: [])
        var tools = ExternalTools.detect(in: locations, ignoring: [])
        #expect(tools.ffmpeg == ownFFmpeg)
        #expect(tools.ffprobe == ownFFprobe)

        let brewFFmpeg = try makeExecutable("ffmpeg", in: homebrew)
        tools = ExternalTools.detect(in: locations, ignoring: [])
        #expect(tools.ffmpeg == brewFFmpeg)
        // Each tool is found on its own.
        #expect(tools.ffprobe == ownFFprobe)
    }

    @Test func nonExecutableFilesDontCount() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        try Data().write(to: folder.appendingPathComponent("ffmpeg"))
        let tools = ExternalTools.detect(in: ToolLocations(commandDirectories: [folder], libreOfficeApps: []), ignoring: [])
        #expect(tools.ffmpeg == nil)
    }

    @Test func libreOfficeInApplicationsComesFirst() throws {
        let system = try makeFolder()
        let user = try makeFolder()
        defer {
            try? FileManager.default.removeItem(at: system)
            try? FileManager.default.removeItem(at: user)
        }
        let systemApp = system.appendingPathComponent("LibreOffice.app")
        let userApp = user.appendingPathComponent("LibreOffice.app")
        for app in [systemApp, userApp] {
            let macOS = app.appendingPathComponent("Contents/MacOS")
            try FileManager.default.createDirectory(at: macOS, withIntermediateDirectories: true)
            _ = try makeExecutable("soffice", in: macOS)
        }
        let tools = ExternalTools.detect(in: ToolLocations(commandDirectories: [], libreOfficeApps: [systemApp, userApp]), ignoring: [])
        #expect(tools.libreOffice == systemApp.appendingPathComponent("Contents/MacOS/soffice"))
    }

    @Test func ignoredToolsActMissing() throws {
        let folder = try makeFolder()
        defer { try? FileManager.default.removeItem(at: folder) }
        for name in ["ffmpeg", "ffprobe", "cwebp"] {
            _ = try makeExecutable(name, in: folder)
        }
        let locations = ToolLocations(commandDirectories: [folder], libreOfficeApps: [])
        let tools = ExternalTools.detect(in: locations, ignoring: ["ffmpeg"])
        #expect(tools.ffmpeg == nil)
        #expect(tools.ffprobe == nil)
        #expect(tools.cwebp != nil)
    }

    @Test func ignoringKeepsOpenShelfsOwnCopies() throws {
        let homebrew = try makeFolder()
        let own = try makeFolder()
        defer {
            try? FileManager.default.removeItem(at: homebrew)
            try? FileManager.default.removeItem(at: own)
        }
        _ = try makeExecutable("ffmpeg", in: homebrew)
        let ownFFmpeg = try makeExecutable("ffmpeg", in: own)
        let locations = ToolLocations(commandDirectories: [homebrew, own], libreOfficeApps: [], ownCommandDirectories: [own])
        // So installing from Settings can be tried on a Mac that has Homebrew's.
        #expect(ExternalTools.detect(in: locations, ignoring: ["ffmpeg"]).ffmpeg == ownFFmpeg)
    }

    @Test func managedToolsLiveInApplicationSupport() {
        #expect(ManagedTools.directory.path.hasSuffix("Library/Application Support/OpenShelf/Tools"))
        #expect(ToolLocations.standard.commandDirectories.last == ManagedTools.directory)
        #expect(ToolLocations.standard.commandDirectories.first?.path == "/opt/homebrew/bin")
    }
}

/// A tool uninstalled while OpenShelf runs gives a clear message.
struct MissingToolErrorTests {
    @Test func runningAMissingToolSaysItIsGone() async {
        await #expect(throws: MissingToolError(name: "ffmpeg")) {
            try await ProcessRunner.run(URL(fileURLWithPath: "/nonexistent/bin/ffmpeg"), ["-version"])
        }
        #expect(MissingToolError(name: "ffmpeg").localizedDescription == "ffmpeg isn’t installed any more.")
        #expect(MissingToolError(executable: URL(fileURLWithPath: "/Applications/LibreOffice.app/Contents/MacOS/soffice")).name == "LibreOffice")
    }

    @Test func conversionsReportItUnchanged() async throws {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("missing-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: folder) }
        let source = folder.appendingPathComponent("Song.ogg")
        try Data("OggS".utf8).write(to: source)
        let gone = ExternalTools(ffmpeg: URL(fileURLWithPath: "/nonexistent/ffmpeg"))
        do {
            _ = try await Converter.convert(source, to: .mp3, tools: gone) { _ in }
            Issue.record("Expected the conversion to fail")
        } catch {
            #expect(error.localizedDescription == "ffmpeg isn’t installed any more.")
        }
        #expect(FailureMessage.describe(MissingToolError(name: "LibreOffice"), source: source) as? MissingToolError == MissingToolError(name: "LibreOffice"))
    }
}
