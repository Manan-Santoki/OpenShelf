import CoreGraphics
import Foundation
import ImageIO
import Testing
import UniformTypeIdentifiers
@testable import OpenShelf

private func isWhole(_ rect: CGRect) -> Bool {
    [rect.minX, rect.minY, rect.width, rect.height].allSatisfy { $0 == $0.rounded() }
}

private func writeImage(width: Int, height: Int, named name: String, in directory: URL) throws -> URL {
    let context = CGContext(
        data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
    )!
    context.setFillColor(CGColor(red: 0.2, green: 0.5, blue: 0.8, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: width, height: height))
    let url = directory.appendingPathComponent(name)
    try EditableImage.write(context.makeImage()!, to: url, type: .png)
    return url
}

private func pixelSize(of url: URL) -> CGSize? {
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
          let width = properties[kCGImagePropertyPixelWidth] as? Int,
          let height = properties[kCGImagePropertyPixelHeight] as? Int
    else { return nil }
    return CGSize(width: width, height: height)
}

/// One crop carried across several images, as Crop's "Same for all" does.
struct BatchCropTests {
    @Test func topHalfStaysTheTopHalfOnEverySize() {
        // Stacked VR panoramas: crop 1:1 down to the top 2:1 half.
        let topHalf = CropMath.unit(CGRect(x: 0, y: 0, width: 4000, height: 2000), in: CGRect(x: 0, y: 0, width: 4000, height: 4000))
        #expect(topHalf == CGRect(x: 0, y: 0, width: 1, height: 0.5))
        #expect(CropMath.mapped(topHalf, to: CGRect(x: 0, y: 0, width: 5760, height: 5760), ratio: nil) == CGRect(x: 0, y: 0, width: 5760, height: 2880))
        let odd = CropMath.mapped(topHalf, to: CGRect(x: 0, y: 0, width: 3001, height: 2999), ratio: nil)
        #expect(isWhole(odd))
        #expect(odd.minX == 0 && odd.minY == 0 && odd.width == 3001)
        #expect(abs(odd.height - 1499.5) <= 0.5)
    }

    @Test func theShownImageKeepsItsCropExactly() {
        // The image being edited gets its own crop back unchanged, so what
        // the size fields show is what gets saved.
        let bounds = CGRect(x: 0, y: 0, width: 1919, height: 1077)
        for crop in [
            CGRect(x: 0, y: 0, width: 1919, height: 1077),
            CGRect(x: 17, y: 3, width: 800, height: 451),
            CGRect(x: 1, y: 1, width: 1, height: 1),
            CGRect(x: 1003, y: 611, width: 916, height: 466),
        ] {
            #expect(CropMath.mapped(CropMath.unit(crop, in: bounds), to: bounds, ratio: nil) == crop)
        }
    }

    @Test func anAspectRatioHoldsOnImagesOfAnotherShape() {
        let wide = CGRect(x: 0, y: 0, width: 1920, height: 1080)
        let crop = CropMath.largest(ratio: 16 / 9, centeredAt: CGPoint(x: 960, y: 540), in: wide)
        let unit = CropMath.unit(crop, in: wide)
        for size in [CGSize(width: 1000, height: 1000), CGSize(width: 600, height: 2000), CGSize(width: 4000, height: 500)] {
            let bounds = CGRect(origin: .zero, size: size)
            let mapped = CropMath.mapped(unit, to: bounds, ratio: 16 / 9)
            #expect(isWhole(mapped))
            #expect(bounds.contains(mapped))
            #expect(abs(mapped.width / mapped.height - 16 / 9) < 0.02)
        }
    }

    @Test func untouchedImagesAreLeftWhole() {
        let whole = CGRect(x: 0, y: 0, width: 1, height: 1)
        for size in [CGSize(width: 1, height: 1), CGSize(width: 333, height: 777), CGSize(width: 8000, height: 6000)] {
            let bounds = CGRect(origin: .zero, size: size)
            #expect(CropMath.mapped(whole, to: bounds, ratio: nil) == bounds)
        }
    }

    @Test func savesEachCroppedImageAndCountsTheRest() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-batchcrop-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }

        let urls = try [(400, 400, "a.png"), (300, 300, "b.png"), (500, 500, "c.png")].map {
            try writeImage(width: $0.0, height: $0.1, named: $0.2, in: directory)
        }
        let images = try urls.map(EditableImage.load)
        let topHalf = CGRect(x: 0, y: 0, width: 1, height: 0.5)
        let jobs = [
            (images[0], CropMath.mapped(topHalf, to: CGRect(origin: .zero, size: images[0].pixelSize), ratio: nil)),
            (images[1], CGRect(origin: .zero, size: images[1].pixelSize)),
            (images[2], CropMath.mapped(topHalf, to: CGRect(origin: .zero, size: images[2].pixelSize), ratio: nil)),
        ]

        let outcome = try ImageCropper.run(jobs)
        #expect(outcome.saved.map(\.lastPathComponent) == ["a Cropped.png", "c Cropped.png"])
        #expect(outcome.unchanged == 1)
        #expect(outcome.title == "2 cropped · 1 not cropped")
        #expect(pixelSize(of: outcome.saved[0]) == CGSize(width: 400, height: 200))
        #expect(pixelSize(of: outcome.saved[1]) == CGSize(width: 500, height: 250))
    }
}
