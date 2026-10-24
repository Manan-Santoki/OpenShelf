import AppKit
import CoreGraphics
import CoreImage
import CoreImage.CIFilterBuiltins
import CoreText
import Darwin
import Foundation
import ImageIO
import PDFKit
import Testing
@testable import OpenShelf

struct ImageRenderingRegressionTests {
    /// A fresh, empty folder.
    func workspace() throws -> URL {
        let folder = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-tools-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        return folder
    }

    /// Black `text` on white, large enough for Vision to read.
    static func picture(of text: String, size: CGSize = CGSize(width: 1200, height: 300)) throws -> CGImage {
        let context = try #require(CGContext(
            data: nil, width: Int(size.width), height: Int(size.height), bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        context.setFillColor(.white)
        context.fill(CGRect(origin: .zero, size: size))
        let font = CTFontCreateWithName("Helvetica" as CFString, 72, nil)
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: [.init(kCTFontAttributeName as String): font]))
        context.textPosition = CGPoint(x: 60, y: 110)
        CTLineDraw(line, context)
        return try #require(context.makeImage())
    }

    /// How far this process's footprint rises while `body` runs, sampled every 20 ms.
    static func peakRise(_ body: () async throws -> Void) async rethrows -> Double {
        func footprint() -> Double {
            var info = rusage_info_v4()
            _ = withUnsafeMutablePointer(to: &info) { $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(getpid(), RUSAGE_INFO_V4, $0) } }
            return Double(info.ri_phys_footprint) / 1_048_576
        }
        let start = footprint()
        let peak = PeakBox(start)
        let sampler = Task.detached {
            while !Task.isCancelled {
                peak.update(footprint())
                try? await Task.sleep(for: .milliseconds(20))
            }
        }
        defer { sampler.cancel() }
        try await body()
        return peak.value - start
    }

    /// PDF pages become images one at a time: all 40 at 300 dpi at once would be about 1.3 GB.
    @Test func pdfPagesAreRenderedOneAtATime() async throws {
        let folder = try workspace()
        defer { try? FileManager.default.removeItem(at: folder) }
        let pdf = folder.appendingPathComponent("Forty.pdf")
        var box = CGRect(x: 0, y: 0, width: 612, height: 792)
        let context = try #require(CGContext(pdf as CFURL, mediaBox: &box, nil))
        let page = try Self.picture(of: "A page")
        for _ in 0..<40 {
            context.beginPDFPage(nil)
            context.draw(page, in: box)
            context.endPDFPage()
        }
        context.closePDF()
        let rise = try await Self.peakRise {
            let images = try await Converter.convert(pdf, to: .png, tools: ExternalTools(ffmpeg: nil, cwebp: nil)) { _ in }
            #expect(images.count == 40)
        }
        #expect(rise < 700, "rose \(Int(rise)) MB")
    }

    // MARK: Shared Core Image contexts

    func pixels(_ image: CGImage) -> Data? {
        image.dataProvider?.data as Data?
    }

    /// Resizing with the shared context gives exactly what a context of its
    /// own did, and the context is made once per colour space.
    @Test func sharedContextsRenderTheSame() throws {
        let picture = try Self.picture(of: "Resize me", size: CGSize(width: 800, height: 300))
        let size = CGSize(width: 333, height: 125)
        let shared = try #require(ImageResizer.resize(picture, to: size))
        let again = try #require(ImageResizer.resize(picture, to: size))

        // What ResizeEditor did before: a fresh context in the image's colour space.
        let filter = CIFilter.lanczosScaleTransform()
        filter.inputImage = CIImage(cgImage: picture).clampedToExtent()
        filter.scale = Float(size.height / CGFloat(picture.height))
        filter.aspectRatio = Float((size.width / CGFloat(picture.width)) / (size.height / CGFloat(picture.height)))
        let rect = CGRect(origin: .zero, size: size)
        let space = picture.colorSpace!
        let fresh = try #require(CIContext(options: [.workingColorSpace: space]).createCGImage(filter.outputImage!.cropped(to: rect), from: rect, format: .RGBA8, colorSpace: space))

        #expect(pixels(shared) == pixels(fresh))
        #expect(pixels(again) == pixels(fresh))
        let sRGB = CGColorSpace(name: CGColorSpace.sRGB)!
        #expect(ImageContexts.working(in: sRGB) === ImageContexts.working(in: sRGB))
        #expect(ImageContexts.working(in: sRGB) !== ImageContexts.working(in: CGColorSpace(name: CGColorSpace.displayP3)!))

        // The standard context (Remove Background's) renders as a fresh default one does.
        let image = CIImage(cgImage: picture).applyingGaussianBlur(sigma: 3).cropped(to: CGRect(x: 0, y: 0, width: 800, height: 300))
        let standard = try #require(ImageContexts.standard.createCGImage(image, from: image.extent, format: .RGBA8, colorSpace: sRGB))
        let plain = try #require(CIContext().createCGImage(image, from: image.extent, format: .RGBA8, colorSpace: sRGB))
        #expect(pixels(standard) == pixels(plain))
    }

}

private final class PeakBox: @unchecked Sendable {
    private let lock = NSLock()
    private var peak: Double
    init(_ start: Double) { peak = start }
    func update(_ value: Double) { lock.withLock { peak = max(peak, value) } }
    var value: Double { lock.withLock { peak } }
}
