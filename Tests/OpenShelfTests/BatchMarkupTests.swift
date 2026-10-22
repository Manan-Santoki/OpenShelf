import CoreGraphics
import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

private func writeImage(width: Int, height: Int, named name: String, in directory: URL) throws -> URL {
    let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    )!
    context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    let url = directory.appendingPathComponent(name)
    try EditableImage.write(context.makeImage()!, to: url, type: .png)
    return url
}

private func makeDirectory() throws -> URL {
    let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-batchmarkup-\(UUID().uuidString)")
    try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    return directory
}

private func pixelSize(of url: URL) -> CGSize? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
          let width = properties[kCGImagePropertyPixelWidth] as? Int,
          let height = properties[kCGImagePropertyPixelHeight] as? Int
    else { return nil }
    return CGSize(width: width, height: height)
}

/// The red, green and blue of the pixel at (x, y), origin top-left, in a saved file.
private func pixel(of url: URL, _ x: Int, _ y: Int) -> (r: UInt8, g: UInt8, b: UInt8)? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil),
          let context = CGContext(
              data: nil, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
              space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
          )
    else { return nil }
    context.draw(image, in: CGRect(x: -x, y: -(image.height - 1 - y), width: image.width, height: image.height))
    let data = context.data!.assumingMemoryBound(to: UInt8.self)
    return (data[0], data[1], data[2])
}

/// Saving several images from Annotate and Redact, where each image has its own
/// marks and images without any are left alone.
struct BatchMarkupTests {
    @Test func annotatesOnlyMarkedImagesAndCountsTheRest() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let urls = try [(400, 300, "a.png"), (200, 200, "b.png"), (640, 480, "c.png")].map {
            try writeImage(width: $0.0, height: $0.1, named: $0.2, in: directory)
        }
        let images = try urls.map(EditableImage.load)
        let red = CGColor(red: 1, green: 0, blue: 0, alpha: 1)
        let jobs = [
            (images[0], [Annotation(figure: .highlight(CGRect(x: 10, y: 10, width: 100, height: 50)), color: red, lineWidth: 4)]),
            (images[1], []),
            (images[2], [
                Annotation(figure: .box(CGRect(x: 100, y: 100, width: 200, height: 200)), color: red, lineWidth: 8),
                Annotation(figure: .arrow(from: CGPoint(x: 20, y: 400), to: CGPoint(x: 300, y: 50)), color: red, lineWidth: 6),
            ]),
        ]

        let outcome = try ImageAnnotator.run(jobs)
        #expect(outcome.saved.map(\.lastPathComponent) == ["a Annotated.png", "c Annotated.png"])
        #expect(outcome.saved.allSatisfy { $0.deletingLastPathComponent().standardizedFileURL == directory.standardizedFileURL })
        #expect(outcome.unchanged == 1)
        #expect(outcome.title == "2 annotated · 1 not annotated")
        #expect(pixelSize(of: outcome.saved[0]) == CGSize(width: 400, height: 300))
        #expect(pixelSize(of: outcome.saved[1]) == CGSize(width: 640, height: 480))

        // The marks are drawn where they were placed, and nowhere else.
        let highlighted = try #require(pixel(of: outcome.saved[0], 50, 30))
        #expect(highlighted.r > 240 && highlighted.g < 200 && highlighted.b < 200)
        let outside = try #require(pixel(of: outcome.saved[0], 300, 200))
        #expect(outside.r > 250 && outside.g > 250 && outside.b > 250)
        let boxEdge = try #require(pixel(of: outcome.saved[1], 100, 200))
        #expect(boxEdge.r > 200 && boxEdge.g < 60 && boxEdge.b < 60)

        // The unmarked image gets no copy, and the originals are untouched.
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("b Annotated.png").path))
        let original = try #require(pixel(of: urls[0], 50, 30))
        #expect(original.r > 250 && original.g > 250 && original.b > 250)
    }

    @Test func redactsOnlyImagesWithAreasAndCountsTheRest() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let urls = try [(300, 200, "one.png"), (500, 400, "two.png"), (120, 90, "three.png")].map {
            try writeImage(width: $0.0, height: $0.1, named: $0.2, in: directory)
        }
        let images = try urls.map(EditableImage.load)
        let box = RedactionLook(style: .box, strength: 50)
        let jobs = [
            (images[0], []),
            (images[1], [RedactRegion(rect: CGRect(x: 50, y: 60, width: 100, height: 40), look: box)]),
            (images[2], []),
        ]

        let outcome = try ImageRedactor.run(jobs)
        #expect(outcome.saved.map(\.lastPathComponent) == ["two Redacted.png"])
        #expect(outcome.saved[0].deletingLastPathComponent().standardizedFileURL == directory.standardizedFileURL)
        #expect(outcome.unchanged == 2)
        #expect(outcome.title == "1 redacted · 2 not redacted")
        #expect(pixelSize(of: outcome.saved[0]) == CGSize(width: 500, height: 400))

        let hidden = try #require(pixel(of: outcome.saved[0], 100, 80))
        #expect(hidden.r < 5 && hidden.g < 5 && hidden.b < 5)
        let shown = try #require(pixel(of: outcome.saved[0], 300, 300))
        #expect(shown.r > 250 && shown.g > 250 && shown.b > 250)
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("one Redacted.png").path))
        #expect(!FileManager.default.fileExists(atPath: directory.appendingPathComponent("three Redacted.png").path))
    }

    @Test func nothingMarkedSavesNothing() throws {
        let directory = try makeDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let urls = try ["x.png", "y.png"].map { try writeImage(width: 64, height: 64, named: $0, in: directory) }
        let images = try urls.map(EditableImage.load)

        let annotated = try ImageAnnotator.run(images.map { ($0, []) })
        #expect(annotated.saved.isEmpty && annotated.unchanged == 2 && !annotated.savedAnything)
        let redacted = try ImageRedactor.run(images.map { ($0, []) })
        #expect(redacted.saved.isEmpty && redacted.unchanged == 2 && !redacted.savedAnything)
        let files = try FileManager.default.contentsOfDirectory(atPath: directory.path)
        #expect(files.sorted() == ["x.png", "y.png"])
    }
}
