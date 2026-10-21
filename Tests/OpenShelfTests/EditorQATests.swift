import CoreGraphics
import Foundation
import ImageIO
import PDFKit
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

private func makeDirectory(_ name: String) throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-\(name)-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory
}

private func makeImage(width: Int, height: Int) -> CGImage {
    let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    )!
    context.setFillColor(CGColor(red: 0.8, green: 0.3, blue: 0.2, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    return context.makeImage()!
}

private func isWhole(_ rect: CGRect) -> Bool {
    [rect.minX, rect.minY, rect.width, rect.height].allSatisfy { $0 == $0.rounded() }
}

struct CropGeometryTests {
    let bounds = CGRect(x: 0, y: 0, width: 1920, height: 1080)

    @Test func typedSizeIsExactAfterAFractionalDrag() {
        // A drag at a fractional view scale used to leave the origin between
        // pixels, so 800 × 450 was saved as 801 × 451.
        let dragged = CropMath.adjusted(bounds, handle: .topLeft, delta: CGSize(width: 37.4, height: 21.6), ratio: nil, bounds: bounds, minimumSize: 16)
        #expect(isWhole(dragged))
        var crop = CropMath.resized(dragged, width: 800, height: nil, ratio: nil, bounds: bounds, minimumSize: 16)
        crop = CropMath.resized(crop, width: nil, height: 450, ratio: nil, bounds: bounds, minimumSize: 16)
        #expect(crop.size == CGSize(width: 800, height: 450))
        #expect(crop.origin == dragged.origin)
        #expect(CropMath.snapped(crop, in: bounds) == crop)
    }

    @Test func handleDragsLandOnWholePixelsInsideTheImage() {
        let start = CGRect(x: 100, y: 100, width: 600, height: 400)
        let deltas = [CGSize(width: 10.3, height: -7.7), CGSize(width: -0.5, height: 0.5), CGSize(width: 5000, height: 5000), CGSize(width: -5000, height: -5000)]
        for handle in CropHandle.allCases {
            for delta in deltas {
                for ratio in [nil, CGFloat(16) / 9, 1] as [CGFloat?] {
                    let crop = CropMath.adjusted(start, handle: handle, delta: delta, ratio: ratio, bounds: bounds, minimumSize: 16)
                    #expect(isWhole(crop), "\(handle) \(delta) \(String(describing: ratio)) → \(crop)")
                    #expect(bounds.contains(crop), "\(handle) \(delta) → \(crop)")
                    #expect(crop.width >= 16 && crop.height >= 16)
                }
            }
        }
    }

    @Test func movingKeepsTheSizeAndStaysInside() {
        let start = CGRect(x: 100, y: 100, width: 640, height: 360)
        let moved = CropMath.adjusted(start, handle: nil, delta: CGSize(width: 12.6, height: -3.2), ratio: nil, bounds: bounds, minimumSize: 16)
        #expect(moved == CGRect(x: 113, y: 97, width: 640, height: 360))
        let pushed = CropMath.adjusted(start, handle: nil, delta: CGSize(width: 9000, height: 9000), ratio: nil, bounds: bounds, minimumSize: 16)
        #expect(pushed == CGRect(x: 1280, y: 720, width: 640, height: 360))
    }

    @Test func typedWidthIsKeptWithARatio() {
        // 815 / (16/9) = 458.4; rounding back must not turn 815 into 814.
        let crop = CropMath.resized(bounds, width: 815, height: nil, ratio: 16 / 9, bounds: bounds, minimumSize: 16)
        #expect(crop.size == CGSize(width: 815, height: 458))
        let square = CropMath.resized(bounds, width: nil, height: 333, ratio: 1, bounds: bounds, minimumSize: 16)
        #expect(square.size == CGSize(width: 333, height: 333))
    }

    @Test func typedSizesThatDontFitAreScaledBack() {
        let start = CGRect(x: 1000, y: 500, width: 200, height: 200)
        let wide = CropMath.resized(start, width: 5000, height: nil, ratio: nil, bounds: bounds, minimumSize: 16)
        #expect(wide == CGRect(x: 0, y: 500, width: 1920, height: 200))
        let tall = CropMath.resized(start, width: 1920, height: nil, ratio: 16 / 9, bounds: bounds, minimumSize: 16)
        #expect(tall == CGRect(x: 0, y: 0, width: 1920, height: 1080))
        let tiny = CropMath.resized(start, width: 2, height: nil, ratio: nil, bounds: bounds, minimumSize: 16)
        #expect(tiny.width == 16)
    }

    @Test func aspectPresetsAreWholePixels() {
        let odd = CGRect(x: 0, y: 0, width: 1001, height: 701)
        let crop = CropMath.largest(ratio: 16 / 9, centeredAt: CGPoint(x: 500.5, y: 350.5), in: odd)
        #expect(isWhole(crop))
        #expect(crop.width == 1001 && crop.height == 563)
        #expect(odd.contains(crop))
    }

    @Test func snappingRoundsEdgesAndClamps() {
        #expect(CropMath.snapped(CGRect(x: 10.4, y: 0.6, width: 99.8, height: 50.1), in: bounds) == CGRect(x: 10, y: 1, width: 100, height: 50))
        #expect(CropMath.snapped(CGRect(x: -4, y: 1070.2, width: 30, height: 30), in: bounds) == CGRect(x: 0, y: 1070, width: 26, height: 10))
    }
}

struct OutputSuffixTests {
    @Test func doesNotRepeatASuffixAlreadyAtTheEnd() throws {
        let directory = try makeDirectory("suffix")
        let compressed = directory.appendingPathComponent("Photo Compressed.jpg")
        FileManager.default.createFile(atPath: compressed.path, contents: Data())
        #expect(OutputNaming.destination(for: compressed, suffix: "Compressed", pathExtension: "jpg").lastPathComponent == "Photo Compressed 2.jpg")
        // A new format has no clash, so no counter either.
        #expect(OutputNaming.destination(for: compressed, suffix: "Compressed", pathExtension: "heic").lastPathComponent == "Photo Compressed.heic")
    }

    @Test func appendsOtherSuffixes() throws {
        let directory = try makeDirectory("suffix-other")
        let photo = directory.appendingPathComponent("Photo.jpg")
        #expect(OutputNaming.destination(for: photo, suffix: "Compressed", pathExtension: "jpg").lastPathComponent == "Photo Compressed.jpg")
        let cropped = directory.appendingPathComponent("Photo Cropped.jpg")
        #expect(OutputNaming.destination(for: cropped, suffix: "Compressed", pathExtension: "jpg").lastPathComponent == "Photo Cropped Compressed.jpg")
    }

    @Test func matchesTheSuffixAsAWholeWordInAnyCase() {
        #expect(OutputNaming.splitSuffix("Compressed", from: "photo compressed") == ("photo", " compressed"))
        #expect(OutputNaming.splitSuffix("Compressed", from: "Decompressed") == ("Decompressed", " Compressed"))
        #expect(OutputNaming.splitSuffix("Compressed", from: "Compressed") == ("Compressed", " Compressed"))
        #expect(OutputNaming.splitSuffix(nil, from: "Photo") == ("Photo", ""))
    }

    @Test func metadataToolsShareTheirNames() throws {
        let directory = try makeDirectory("pdf-metadata")
        let url = directory.appendingPathComponent("Report.pdf")
        let document = PDFDocument()
        document.insert(PDFPage(), at: 0)
        document.documentAttributes = [PDFDocumentAttribute.titleAttribute: "Old"]
        #expect(document.write(to: url))

        var fields = PDFMetadataFields()
        fields.title = "New"
        #expect(try PDFTools.saveMetadata(url, fields: fields).lastPathComponent == "Report Metadata.pdf")
        #expect(try PDFTools.saveMetadata(url, fields: nil).lastPathComponent == "Report Clean.pdf")
        // The Pages editor's "Edited" is no longer shared.
        #expect(MetadataSuffix.edited != "Edited")
    }
}

struct SaveOutcomeTests {
    private let mountain = URL(fileURLWithPath: "/tmp/Mountain Resized 2.jpg")

    @Test func saysWhatHappenedToEachFile() {
        let mixed = SaveOutcome(saved: [mountain], unchanged: 2, savedLabel: "resized", unchangedLabel: "already smaller")
        #expect(mixed.title == "1 resized · 2 already smaller")
        #expect(mixed.subtitle == "Mountain Resized 2.jpg")
        #expect(mixed.savedAnything)
    }

    @Test func plainSaves() {
        #expect(SaveOutcome(saved: [mountain]).title == "Saved")
        #expect(SaveOutcome(saved: [mountain]).subtitle == "Mountain Resized 2.jpg")
        let three = SaveOutcome(saved: [mountain, mountain, mountain], detail: "as PNG")
        #expect(three.title == "Saved 3 files")
        #expect(three.subtitle == "as PNG")
    }

    @Test func nothingSaved() {
        let one = SaveOutcome(saved: [], unchanged: 1, savedLabel: "compressed", unchangedLabel: "can’t get smaller")
        #expect(one.title == "Nothing saved")
        #expect(one.subtitle == "Can’t get smaller")
        #expect(!one.savedAnything)
        let three = SaveOutcome(saved: [], unchanged: 3, savedLabel: "cleaned", unchangedLabel: "already clean")
        #expect(three.subtitle == "All 3 already clean")
    }

    @Test func resizingSeveralImagesCountsTheSmallerOnes() throws {
        let directory = try makeDirectory("resize-batch")
        var images: [EditableImage] = []
        for (name, width) in [("Mountain", 2000), ("Lake", 800), ("Forest", 600)] {
            let url = directory.appendingPathComponent("\(name).png")
            try EditableImage.write(makeImage(width: width, height: width / 2), to: url, type: .png)
            images.append(try EditableImage.load(url))
        }
        let rule = ResizeRule(mode: .longEdge, longEdge: 1080)
        #expect(images.filter { rule.changes($0.pixelSize) }.count == 1)
        let outcome = try ImageResizer.run(images, rule: rule)
        #expect(outcome.saved.map(\.lastPathComponent) == ["Mountain Resized.png"])
        #expect(outcome.title == "1 resized · 2 already smaller")
    }

    @Test func removingMetadataSkipsCleanImages() throws {
        let directory = try makeDirectory("metadata-batch")
        let plain = directory.appendingPathComponent("Plain.png")
        try EditableImage.write(makeImage(width: 40, height: 30), to: plain, type: .png)
        let tagged = directory.appendingPathComponent("Tagged.jpg")
        let gps: [CFString: Any] = [
            kCGImagePropertyGPSLatitude: 33.42, kCGImagePropertyGPSLatitudeRef: "N",
            kCGImagePropertyGPSLongitude: 111.93, kCGImagePropertyGPSLongitudeRef: "W",
        ]
        try EditableImage.write(makeImage(width: 40, height: 30), to: tagged, type: .jpeg, metadata: [kCGImagePropertyGPSDictionary: gps])
        #expect(!MetadataStripper.hasIdentifyingMetadata(at: plain))
        #expect(MetadataStripper.hasIdentifyingMetadata(at: tagged))

        let outcome = try MetadataStripper.run([try EditableImage.load(plain), try EditableImage.load(tagged)], keepsOrientationAndProfile: true)
        #expect(outcome.saved.map(\.lastPathComponent) == ["Tagged Clean.jpg"])
        #expect(outcome.title == "1 cleaned · 1 already clean")
    }

    @Test func removingPDFMetadataSkipsCleanFiles() throws {
        let directory = try makeDirectory("pdf-batch")
        let tagged = directory.appendingPathComponent("Tagged.pdf")
        let document = PDFDocument()
        document.insert(PDFPage(), at: 0)
        document.documentAttributes = [PDFDocumentAttribute.authorAttribute: "Someone"]
        #expect(document.write(to: tagged))
        // A copy that was already cleaned has nothing left to remove.
        let clean = try PDFTools.saveMetadata(tagged, fields: nil)
        #expect(PDFTools.info(for: clean)?.hasMetadata == false)

        let outcome = try PDFTools.removeMetadata([clean, tagged])
        #expect(outcome.saved.map(\.lastPathComponent) == ["Tagged Clean 2.pdf"])
        #expect(outcome.title == "1 cleaned · 1 already clean")
    }
}

struct NoChangeTests {
    @Test func adjustmentsThatCancelOutAreNoChange() {
        #expect(!AdjustSettings().changesImage)
        #expect(AdjustSettings(exposure: 4).changesImage)
        #expect(!AdjustSettings(exposure: 0.3).changesImage)
        #expect(!AdjustSettings(quarterTurns: 4).changesImage)
        #expect(!AdjustSettings(quarterTurns: -4).changesImage)
        #expect(AdjustSettings(quarterTurns: 1).changesImage)
        #expect(AdjustSettings(flipsHorizontally: true).changesImage)
        // Both flips are a half turn, so with another half turn nothing changes.
        #expect(!AdjustSettings(quarterTurns: 2, flipsHorizontally: true, flipsVertically: true).changesImage)
        #expect(AdjustSettings(flipsHorizontally: true, flipsVertically: true).changesImage)
    }

    @Test func backgroundWithNoMarginOrCornersIsNoChange() {
        let size = CGSize(width: 1600, height: 900)
        var composition = BackgroundComposition(paint: .solid(.white), padding: 0, cornerRadius: 0, shadow: 30, ratio: .auto)
        #expect(!composition.changesImage(of: size, isTransparent: false))
        #expect(composition.changesImage(of: size, isTransparent: true))
        composition.ratio = .landscape
        #expect(!composition.changesImage(of: size, isTransparent: false))
        composition.ratio = .square
        #expect(composition.changesImage(of: size, isTransparent: false))
        composition.ratio = .auto
        composition.cornerRadius = 12
        #expect(composition.changesImage(of: size, isTransparent: false))
        composition.cornerRadius = 0
        composition.padding = 40
        #expect(composition.changesImage(of: size, isTransparent: false))
    }
}

struct ArchiveTests {
    private func makeAlbum(in directory: URL, named name: String = "Album") throws -> URL {
        let album = directory.appendingPathComponent(name)
        let sub = album.appendingPathComponent("Sub")
        try FileManager.default.createDirectory(at: sub, withIntermediateDirectories: true)
        try Data("one".utf8).write(to: album.appendingPathComponent("café.txt"))
        try Data("two".utf8).write(to: sub.appendingPathComponent("two.txt"))
        try Data("x".utf8).write(to: album.appendingPathComponent(".DS_Store"))
        try Data("x".utf8).write(to: sub.appendingPathComponent(".DS_Store"))
        try Data("x".utf8).write(to: sub.appendingPathComponent("._two.txt"))
        try Data("hidden".utf8).write(to: album.appendingPathComponent(".keep"))
        // An extended attribute is what `ditto --sequesterRsrc` turned into "__MACOSX/._café.txt".
        let path = album.appendingPathComponent("café.txt").path
        _ = setxattr(path, "com.openshelf.test", "value", 5, 0, 0)
        return album
    }

    private func entries(of archive: URL) throws -> [String] {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zipinfo")
        process.arguments = ["-1", archive.path]
        // Without a UTF-8 locale, zipinfo prints "?" for accented letters.
        process.environment = ["LC_ALL": "en_US.UTF-8"]
        let pipe = Pipe()
        process.standardOutput = pipe
        try process.run()
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return String(decoding: data, as: UTF8.self).split(separator: "\n").map(String.init).sorted()
    }

    private func tree(_ folder: URL) -> [String] {
        (FileManager.default.enumerator(atPath: folder.path)?.allObjects as? [String] ?? []).sorted()
    }

    @Test func recognisesMacMetadata() {
        #expect(ArchiveConversion.isMacMetadata(".DS_Store"))
        #expect(ArchiveConversion.isMacMetadata("._photo.jpg"))
        #expect(ArchiveConversion.isMacMetadata("__MACOSX"))
        #expect(!ArchiveConversion.isMacMetadata(".keep"))
        #expect(!ArchiveConversion.isMacMetadata("Album"))
    }

    @Test func zipLeavesOutMacMetadata() async throws {
        let directory = try makeDirectory("zip")
        let album = try makeAlbum(in: directory)
        let archive = try await ArchiveConversion.zip([album])
        #expect(archive.lastPathComponent == "Album.zip")
        let names = try entries(of: archive)
        #expect(names == ["Album/", "Album/.keep", "Album/Sub/", "Album/Sub/two.txt", "Album/café.txt"])
    }

    @Test func zipOfSeveralItemsKeepsFolders() async throws {
        let directory = try makeDirectory("zip-several")
        let album = try makeAlbum(in: directory)
        let note = directory.appendingPathComponent("Note.txt")
        try Data("note".utf8).write(to: note)
        let archive = try await ArchiveConversion.zip([album, note])
        #expect(archive.lastPathComponent == "Archive.zip")
        #expect(try entries(of: archive).contains("Album/Sub/two.txt"))
        #expect(try entries(of: archive).contains("Note.txt"))
        #expect(try !entries(of: archive).contains { $0.contains(".DS_Store") || $0.contains("__MACOSX") })
    }

    @Test func tarLeavesOutMacMetadata() async throws {
        let directory = try makeDirectory("tar")
        let album = try makeAlbum(in: directory)
        let archive = try await ArchiveConversion.tar([album], gzip: true)
        let extracted = try await ArchiveConversion.extract(archive)
        #expect(extracted.lastPathComponent == "Album 2")
        #expect(tree(extracted) == [".keep", "Sub", "Sub/two.txt", "café.txt"])
    }

    @Test func singleFolderArchiveExtractsBesideIt() async throws {
        let directory = try makeDirectory("unzip-single")
        let album = try makeAlbum(in: directory)
        let archive = try await ArchiveConversion.zip([album])
        try FileManager.default.removeItem(at: album)

        let extracted = try await ArchiveConversion.extract(archive)
        #expect(extracted.path == directory.appendingPathComponent("Album").path)
        #expect(tree(extracted) == [".keep", "Sub", "Sub/two.txt", "café.txt"])
        // Taken names get a number, and no staging folder is left behind.
        let again = try await ArchiveConversion.extract(archive)
        #expect(again.lastPathComponent == "Album 2")
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted() == ["Album", "Album 2", "Album.zip"])
    }

    @Test func zipFromFinderWithMacOSXExtractsAsOneFolder() async throws {
        let directory = try makeDirectory("unzip-finder")
        let album = try makeAlbum(in: directory)
        // Finder's Compress, like ditto, stores resource forks under "__MACOSX".
        let archive = directory.appendingPathComponent("Holiday.zip")
        try await ProcessRunner.run(URL(fileURLWithPath: "/usr/bin/ditto"), ["-c", "-k", "--sequesterRsrc", "--keepParent", album.path, archive.path])
        #expect(try entries(of: archive).contains { $0.hasPrefix("__MACOSX/") })
        try FileManager.default.removeItem(at: album)

        let extracted = try await ArchiveConversion.extract(archive)
        #expect(extracted.path == album.path)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted() == ["Album", "Holiday.zip"])
    }

    @Test func archiveWithSeveralItemsExtractsIntoAFolder() async throws {
        let directory = try makeDirectory("unzip-several")
        let album = try makeAlbum(in: directory)
        let note = directory.appendingPathComponent("Note.txt")
        try Data("note".utf8).write(to: note)
        let archive = try await ArchiveConversion.zip([album, note])
        let extracted = try await ArchiveConversion.extract(archive)
        #expect(extracted.lastPathComponent == "Archive")
        #expect(try FileManager.default.contentsOfDirectory(atPath: extracted.path).sorted() == ["Album", "Note.txt"])

        let tarball = try await ArchiveConversion.tar([album, note], gzip: false)
        let fromTar = try await ArchiveConversion.extract(tarball)
        #expect(fromTar.lastPathComponent == "Archive 2")
    }

    @Test func singleFolderIgnoresMacOSXAndDSStore() throws {
        let directory = try makeDirectory("place")
        let staging = directory.appendingPathComponent(".staging")
        try FileManager.default.createDirectory(at: staging.appendingPathComponent("Album"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: staging.appendingPathComponent("__MACOSX/Album"), withIntermediateDirectories: true)
        try Data("x".utf8).write(to: staging.appendingPathComponent("__MACOSX/Album/._a"))
        try Data("x".utf8).write(to: staging.appendingPathComponent(".DS_Store"))
        try FileManager.default.createDirectory(at: directory.appendingPathComponent("Album"), withIntermediateDirectories: true)

        let placed = try ArchiveConversion.placeExtracted(staging, archiveName: "Photos", in: directory)
        #expect(placed.lastPathComponent == "Album 2")
        #expect(!FileManager.default.fileExists(atPath: staging.path))
    }

    @Test func singleFileStillGetsAFolder() throws {
        let directory = try makeDirectory("place-file")
        let staging = directory.appendingPathComponent(".staging")
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        try Data("x".utf8).write(to: staging.appendingPathComponent("Readme.txt"))
        let placed = try ArchiveConversion.placeExtracted(staging, archiveName: "Docs", in: directory)
        #expect(placed.lastPathComponent == "Docs")
        #expect(try FileManager.default.contentsOfDirectory(atPath: placed.path) == ["Readme.txt"])
    }

    @Test func packagesKeepTheirExtensionWhenRenamed() throws {
        let directory = try makeDirectory("place-app")
        try FileManager.default.createDirectory(at: directory.appendingPathComponent("Tool.app/Contents"), withIntermediateDirectories: true)
        let staging = directory.appendingPathComponent(".staging")
        try FileManager.default.createDirectory(at: staging.appendingPathComponent("Tool.app/Contents"), withIntermediateDirectories: true)
        let placed = try ArchiveConversion.placeExtracted(staging, archiveName: "Tool", in: directory)
        #expect(placed.lastPathComponent == "Tool 2.app")
    }
}

struct PDFMetadataDefinitionTests {
    private func info(_ fields: PDFMetadataFields, created: Date? = nil) -> PDFInfo {
        PDFInfo(fields: fields, pageCount: 1, fileSize: 1, created: created, modified: created, isEncrypted: false, isLocked: false)
    }

    @Test func macOSSaveStampsDontCountAsMetadata() {
        var fields = PDFMetadataFields()
        fields.producer = "macOS Version 15.1 (Build 24B83) Quartz PDFContext"
        #expect(!info(fields, created: Date()).hasMetadata)
    }

    @Test func authorDetailsAndOtherProducersCount() {
        var authored = PDFMetadataFields()
        authored.author = "Someone"
        #expect(info(authored).hasMetadata)

        var exported = PDFMetadataFields()
        exported.producer = "Microsoft® Word for Microsoft 365"
        #expect(info(exported).hasMetadata)
    }
}
