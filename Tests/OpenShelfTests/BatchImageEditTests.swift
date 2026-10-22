import CoreGraphics
import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

private func makeDirectory() throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-batchedit-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory
}

private func writeImage(width: Int, height: Int, named name: String, type: UTType = .png, in directory: URL) throws -> EditableImage {
    let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    )!
    context.setFillColor(CGColor(red: 0.5, green: 0.5, blue: 0.5, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    let url = directory.appendingPathComponent(name)
    try EditableImage.write(context.makeImage()!, to: url, type: type)
    return try EditableImage.load(url)
}

private func pixelSize(of url: URL) -> CGSize? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
          let width = properties[kCGImagePropertyPixelWidth] as? Int,
          let height = properties[kCGImagePropertyPixelHeight] as? Int
    else { return nil }
    return CGSize(width: width, height: height)
}

private func fileType(of url: URL) -> UTType? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let identifier = CGImageSourceGetType(source) as String?
    else { return nil }
    return UTType(identifier)
}

/// Saving several images from Edit, Add BG and Remove BG at once.
struct BatchImageEditTests {
    @Test func editSavesEachChangedImageAndCountsTheRest() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let wide = try writeImage(width: 400, height: 200, named: "wide.png", in: directory)
        let photo = try writeImage(width: 300, height: 100, named: "photo.jpg", type: .jpeg, in: directory)
        let square = try writeImage(width: 50, height: 50, named: "square.png", in: directory)

        let outcome = try ImageAdjuster.run([
            (wide, AdjustSettings(brightness: 20, quarterTurns: 1)),
            // Four turns leave the image as it was.
            (square, AdjustSettings(quarterTurns: 4)),
            (photo, AdjustSettings(contrast: 30)),
        ])
        #expect(outcome.saved.map(\.lastPathComponent) == ["wide Edited.png", "photo Edited.jpg"])
        #expect(outcome.unchanged == 1)
        #expect(outcome.title == "2 edited · 1 not edited")
        // A quarter turn swaps the sides.
        #expect(pixelSize(of: outcome.saved[0]) == CGSize(width: 200, height: 400))
        #expect(pixelSize(of: outcome.saved[1]) == CGSize(width: 300, height: 100))
        #expect(fileType(of: outcome.saved[1]) == .jpeg)
    }

    @Test func editWithNothingToChangeSavesNothing() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let image = try writeImage(width: 20, height: 20, named: "a.png", in: directory)

        let outcome = try ImageAdjuster.run([(image, AdjustSettings()), (image, AdjustSettings(exposure: 0.2))])
        #expect(outcome.saved.isEmpty)
        #expect(outcome.unchanged == 2)
        #expect(!outcome.savedAnything)
    }

    @Test func addBackgroundSavesPaddedPNGsAndCountsTheRest() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let photo = try writeImage(width: 300, height: 200, named: "photo.jpg", type: .jpeg, in: directory)
        let icon = try writeImage(width: 100, height: 100, named: "icon.png", in: directory)
        let plain = try writeImage(width: 80, height: 60, named: "plain.png", in: directory)

        let padded = BackgroundComposition(paint: .solid(.white), padding: 50, cornerRadius: 24, shadow: 24, ratio: .auto)
        let square = BackgroundComposition(paint: .gradient(GradientStyle.all[0]), padding: 10, cornerRadius: 0, shadow: 0, ratio: .square)
        // No margin, no corners and an opaque image: the result would look the same.
        let none = BackgroundComposition(paint: .solid(.white), padding: 0, cornerRadius: 0, shadow: 30, ratio: .auto)

        let outcome = try BackgroundAdder.run([(photo, padded), (icon, square), (plain, none)])
        #expect(outcome.saved.map(\.lastPathComponent) == ["photo With Background.png", "icon With Background.png"])
        #expect(outcome.unchanged == 1)
        #expect(outcome.title == "2 saved · 1 unchanged")
        #expect(pixelSize(of: outcome.saved[0]) == CGSize(width: 400, height: 300))
        #expect(pixelSize(of: outcome.saved[1]) == CGSize(width: 120, height: 120))
        // Always PNG, so rounded corners stay see-through.
        #expect(outcome.saved.map { fileType(of: $0) } == [.png, .png])
    }

    @Test func removeBackgroundCountsImagesWithNoSubject() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let images = try ["a.png", "b.jpg"].map {
            try writeImage(width: 256, height: 256, named: $0, type: $0.hasSuffix("jpg") ? .jpeg : .png, in: directory)
        }

        let outcome = try BackgroundRemover.run(images)
        #expect(outcome.saved.isEmpty)
        #expect(outcome.unchanged == 2)
        #expect(outcome.title == "Nothing saved")
        #expect(outcome.subtitle == "All 2 without a subject")
    }

    @Test func removeBackgroundSavesTransparentPNGs() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let photo = try writeImage(width: 120, height: 90, named: "photo.jpg", type: .jpeg, in: directory)

        let url = try BackgroundRemover.save(photo.cgImage, of: photo)
        #expect(url.lastPathComponent == "photo No Background.png")
        #expect(url.deletingLastPathComponent().standardizedFileURL == directory.standardizedFileURL)
        #expect(fileType(of: url) == .png)
        #expect(pixelSize(of: url) == CGSize(width: 120, height: 90))
    }
}
