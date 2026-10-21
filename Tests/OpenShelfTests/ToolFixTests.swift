import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

private func makeDirectory(_ name: String) throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-\(name)-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory
}

/// A small opaque or see-through test image.
private func makeImage(width: Int = 64, height: Int = 48, transparent: Bool = false) -> CGImage {
    let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    )!
    context.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.9, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    if transparent {
        context.clear(CGRect(x: 0, y: 0, width: width / 2, height: height))
    }
    return context.makeImage()!
}

/// Writes `image` with a GPS position and camera details.
private func writeWithLocation(_ image: CGImage, to url: URL, type: UTType) {
    let destination = CGImageDestinationCreateWithURL(url as CFURL, type.identifier as CFString, 1, nil)!
    let properties: [CFString: Any] = [
        kCGImagePropertyGPSDictionary: [
            kCGImagePropertyGPSLatitude: 33.42, kCGImagePropertyGPSLatitudeRef: "N",
            kCGImagePropertyGPSLongitude: 111.93, kCGImagePropertyGPSLongitudeRef: "W",
        ],
        kCGImagePropertyTIFFDictionary: [kCGImagePropertyTIFFMake: "TestCam", kCGImagePropertyTIFFModel: "One"],
        kCGImagePropertyExifDictionary: [kCGImagePropertyExifDateTimeOriginal: "2026:10:20 10:00:00"],
    ]
    CGImageDestinationAddImage(destination, image, properties as CFDictionary)
    CGImageDestinationFinalize(destination)
}

private func hasGPS(_ url: URL) -> Bool {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
          let gps = properties[kCGImagePropertyGPSDictionary] as? [CFString: Any]
    else { return false }
    return !gps.isEmpty
}

struct MetadataStrippingTests {
    @Test(arguments: [UTType.heic, .jpeg, .png])
    func removesLocationAndCameraDetails(type: UTType) throws {
        let directory = try makeDirectory("strip")
        let url = directory.appendingPathComponent("Photo.\(type.preferredFilenameExtension!)")
        writeWithLocation(makeImage(), to: url, type: type)
        #expect(hasGPS(url) || type == .png)

        for keeps in [true, false] {
            let clean = try MetadataStripper.strip(try EditableImage.load(url), keepsOrientationAndProfile: keeps)
            #expect(!hasGPS(clean))
            #expect(MetadataStripper.leftoverMetadata(at: clean).isEmpty)
            #expect(clean.pathExtension == url.pathExtension)
        }
    }

    @Test func detectsLeftoverLocation() throws {
        let directory = try makeDirectory("leftover")
        let url = directory.appendingPathComponent("Photo.jpg")
        writeWithLocation(makeImage(), to: url, type: .jpeg)
        #expect(MetadataStripper.leftoverMetadata(at: url).contains("location"))
    }
}

struct VectorImageTests {
    @Test func loadsSVGAsPixels() throws {
        let directory = try makeDirectory("svg")
        let url = directory.appendingPathComponent("Logo.svg")
        try ##"<svg xmlns="http://www.w3.org/2000/svg" width="200" height="100"><rect width="200" height="100" fill="#3366ff"/></svg>"##
            .write(to: url, atomically: true, encoding: .utf8)

        let image = try EditableImage.load(url)
        #expect(max(image.cgImage.width, image.cgImage.height) >= 1024)
        #expect(max(image.cgImage.width, image.cgImage.height) <= 4096)
        #expect(image.cgImage.width == image.cgImage.height * 2)

        let saved = try image.save(image.cgImage, suffix: "Edited")
        #expect(saved.pathExtension == "png")
    }
}

struct CompressFormatTests {
    @Test func keepOriginalKeepsWhatItCanAndKeepsTransparency() {
        let keep = CompressOptions.Format.keepOriginal
        #expect(ImageCompressor.outputType(sourceType: .png, isTransparent: true, format: keep) == .png)
        #expect(ImageCompressor.outputType(sourceType: .jpeg, isTransparent: false, format: keep) == .jpeg)
        #expect(ImageCompressor.outputType(sourceType: .heic, isTransparent: false, format: keep) == .heic)
        #expect(ImageCompressor.outputType(sourceType: .gif, isTransparent: false, format: keep) == .jpeg)
        #expect(ImageCompressor.outputType(sourceType: .webP, isTransparent: true, format: keep) == .heic)
        #expect(ImageCompressor.outputType(sourceType: .tiff, isTransparent: false, format: keep) == .jpeg)
        #expect(ImageCompressor.outputType(sourceType: .png, isTransparent: true, format: .jpeg) == .jpeg)
    }

    @Test func usesJPGSpelling() {
        let png = EditableImage(url: URL(fileURLWithPath: "/tmp/Sticker.png"), cgImage: makeImage(), properties: [:], type: .png)
        #expect(png.pathExtension(for: .jpeg) == "jpg")
        #expect(png.pathExtension(for: .png) == "png")
        let upper = EditableImage(url: URL(fileURLWithPath: "/tmp/Photo.JPEG"), cgImage: makeImage(), properties: [:], type: .jpeg)
        #expect(upper.pathExtension(for: .jpeg) == "JPEG")
    }

    @Test func detectsRealTransparency() {
        let opaqueWithAlphaChannel = EditableImage(url: URL(fileURLWithPath: "/tmp/a.png"), cgImage: makeImage(), properties: [:], type: .png)
        let seeThrough = EditableImage(url: URL(fileURLWithPath: "/tmp/b.png"), cgImage: makeImage(transparent: true), properties: [:], type: .png)
        #expect(!opaqueWithAlphaChannel.isTransparent)
        #expect(seeThrough.isTransparent)
    }

    @Test func notesWhenTheFormatChanges() throws {
        let directory = try makeDirectory("note")
        let url = directory.appendingPathComponent("Clip.gif")
        let destination = CGImageDestinationCreateWithURL(url as CFURL, UTType.gif.identifier as CFString, 2, nil)!
        CGImageDestinationAddImage(destination, makeImage(), nil)
        CGImageDestinationAddImage(destination, makeImage(transparent: true), nil)
        CGImageDestinationFinalize(destination)
        let image = try EditableImage.load(url)
        let note = ImageCompressor.formatNote(for: [image], format: .keepOriginal) ?? ""
        #expect(note.contains("Animation isn’t kept"))
        #expect(note.contains("GIF"))
    }
}

struct NotSmallerTests {
    @Test func onlySavesWhenSmaller() {
        #expect(!ImageCompressor.isWorthSaving(100, originalSize: 100))
        #expect(!ImageCompressor.isWorthSaving(150, originalSize: 100))
        #expect(ImageCompressor.isWorthSaving(99, originalSize: 100))
        #expect(ImageCompressor.isWorthSaving(5, originalSize: 0))
    }

    @Test func compressingTwiceSavesNothingTheSecondTime() throws {
        let directory = try makeDirectory("twice")
        let url = directory.appendingPathComponent("Dot.png")
        try EditableImage.write(makeImage(width: 32, height: 32), to: url, type: .png)
        let first = try ImageCompressor.save(try EditableImage.load(url), options: CompressOptions()) ?? url
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted()
        let second = try ImageCompressor.save(try EditableImage.load(first), options: CompressOptions())
        #expect(second == nil)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.path).sorted() == files)
    }

    @Test func messagesForSkippedFiles() {
        #expect(CompressError.notSmaller(count: 1).localizedDescription == "Already as small as it gets — nothing saved.")
        #expect(PDFToolError.notSmaller(count: 3).localizedDescription.contains("These 3 PDFs"))
    }
}

struct SaveErrorTests {
    private let target = URL(fileURLWithPath: "/tmp/Reports/Chart.png")

    @Test func mapsCocoaAndPOSIXErrors() {
        #expect(SaveError.from(NSError(domain: NSCocoaErrorDomain, code: NSFileWriteNoPermissionError), writing: target) == .noPermission(folder: "Reports"))
        #expect(SaveError.from(NSError(domain: NSPOSIXErrorDomain, code: Int(ENOSPC)), writing: target) == .diskFull)
        #expect(SaveError.from(NSError(domain: NSCocoaErrorDomain, code: NSFileWriteVolumeReadOnlyError), writing: target) == .readOnlyVolume(folder: "Reports"))
        let wrapped = NSError(domain: NSCocoaErrorDomain, code: 0, userInfo: [NSUnderlyingErrorKey: NSError(domain: NSPOSIXErrorDomain, code: Int(EACCES))])
        #expect(SaveError.from(wrapped, writing: target) == .noPermission(folder: "Reports"))
    }

    @Test func explainsReadOnlyAndMissingFolders() throws {
        let directory = try makeDirectory("readonly")
        let locked = directory.appendingPathComponent("Locked")
        try FileManager.default.createDirectory(at: locked, withIntermediateDirectories: true)
        try FileManager.default.setAttributes([.posixPermissions: 0o555], ofItemAtPath: locked.path)
        defer { try? FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: locked.path) }

        #expect(throws: SaveError.noPermission(folder: "Locked")) {
            try EditableImage.write(makeImage(), to: locked.appendingPathComponent("Out.png"), type: .png)
        }
        #expect(throws: SaveError.folderMissing(folder: "Gone")) {
            try SaveError.checkWritable(directory.appendingPathComponent("Gone/Out.png"))
        }
        #expect(SaveError.noPermission(folder: "Locked").localizedDescription == "You don’t have permission to save in “Locked”.")
    }

    @Test func rejectsNamesTooLongToSave() throws {
        let directory = try makeDirectory("long")
        let name = String(repeating: "L", count: 245) + ".png"
        #expect(throws: SaveError.nameTooLong(file: name)) {
            try SaveError.checkWritable(directory.appendingPathComponent(name))
        }
    }
}
