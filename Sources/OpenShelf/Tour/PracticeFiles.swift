import AppKit
import AVFoundation
import CoreText
import UniformTypeIdentifiers

/// A file the tour gives people to practise on.
enum PracticeFile: String, CaseIterable, Identifiable, Sendable {
    case photo
    case notes
    case report
    case clip
    case letter

    var id: String { rawValue }

    var fileName: String {
        switch self {
        case .photo: "Mountain Lake.jpg"
        case .notes: "Trip Notes.md"
        case .report: "Field Report.pdf"
        case .clip: "Sunset.mov"
        case .letter: "Letter.txt"
        }
    }

    /// What it is, under its name in the tray.
    var caption: String {
        switch self {
        case .photo: "Photo"
        case .notes: "Markdown"
        case .report: "3-page PDF"
        case .clip: "4-second video"
        case .letter: "Plain text"
        }
    }
}

/// Makes the practice files in their own folder, drawn and encoded here since
/// the app carries no sample files. Anything the person makes from them is
/// saved beside them, so the folder shows their progress.
enum PracticeFiles {
    /// ~/Library/Application Support/OpenShelf/Practice.
    static var folder: URL {
        ManagedTools.directory.deletingLastPathComponent().appendingPathComponent("Practice", isDirectory: true)
    }

    static func url(of file: PracticeFile, in folder: URL = folder) -> URL {
        folder.appendingPathComponent(file.fileName)
    }

    /// Creates whichever practice files are missing from `folder`.
    static func prepare(in folder: URL = folder) async throws {
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        for file in PracticeFile.allCases {
            let url = url(of: file, in: folder)
            guard !FileManager.default.fileExists(atPath: url.path) else { continue }
            switch file {
            case .photo: try writePhoto(to: url)
            case .notes: try notes.write(to: url, atomically: true, encoding: .utf8)
            case .report: try writeReport(to: url)
            case .clip: try await writeClip(to: url)
            case .letter: try letter.write(to: url, atomically: true, encoding: .utf8)
            }
        }
    }

    /// Everything in the folder that isn't one of the practice files: what
    /// the person made, newest first.
    static func results(in folder: URL = folder) -> [URL] {
        let originals = Set(PracticeFile.allCases.map(\.fileName))
        let items = (try? FileManager.default.contentsOfDirectory(
            at: folder,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        )) ?? []
        return items
            .filter { !originals.contains($0.lastPathComponent) }
            .sorted { modified($0) > modified($1) }
    }

    /// Deletes the folder and everything made in it.
    static func remove(_ folder: URL = folder) throws {
        guard FileManager.default.fileExists(atPath: folder.path) else { return }
        try FileManager.default.trashItem(at: folder, resultingItemURL: nil)
    }

    private static func modified(_ url: URL) -> Date {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
    }

    // MARK: The photo

    /// A landscape at dusk: sky, sun, mountains and their reflection.
    static func photoImage(width: Int = 1600, height: Int = 1066) -> CGImage? {
        guard let context = CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
        ) else { return nil }
        let w = CGFloat(width)
        let h = CGFloat(height)
        let horizon = h * 0.42

        func gradient(_ colors: [(CGFloat, CGFloat, CGFloat)], from start: CGPoint, to end: CGPoint) {
            let cgColors = colors.map { CGColor(srgbRed: $0.0, green: $0.1, blue: $0.2, alpha: 1) } as CFArray
            guard let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: cgColors, locations: nil) else { return }
            context.drawLinearGradient(gradient, start: start, end: end, options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        }

        // Sky, warm at the horizon.
        context.saveGState()
        context.clip(to: CGRect(x: 0, y: horizon, width: w, height: h - horizon))
        gradient([(0.99, 0.62, 0.42), (0.86, 0.45, 0.55), (0.26, 0.24, 0.52)], from: CGPoint(x: 0, y: horizon), to: CGPoint(x: 0, y: h))
        context.restoreGState()

        // The sun, low over the ridge.
        context.setFillColor(CGColor(srgbRed: 1, green: 0.88, blue: 0.62, alpha: 1))
        context.fillEllipse(in: CGRect(x: w * 0.62, y: horizon + h * 0.05, width: h * 0.16, height: h * 0.16))

        // Two ranges of mountains.
        func ridge(_ points: [(CGFloat, CGFloat)], color: (CGFloat, CGFloat, CGFloat), mirrored: Bool) {
            let path = CGMutablePath()
            path.move(to: CGPoint(x: 0, y: horizon))
            for (x, y) in points {
                path.addLine(to: CGPoint(x: w * x, y: mirrored ? horizon - h * y * 0.8 : horizon + h * y))
            }
            path.addLine(to: CGPoint(x: w, y: horizon))
            path.closeSubpath()
            context.addPath(path)
            context.setFillColor(CGColor(srgbRed: color.0, green: color.1, blue: color.2, alpha: mirrored ? 0.55 : 1))
            context.fillPath()
        }
        let far: [(CGFloat, CGFloat)] = [(0.0, 0.12), (0.14, 0.26), (0.28, 0.16), (0.45, 0.33), (0.6, 0.18), (0.78, 0.29), (1.0, 0.14)]
        let near: [(CGFloat, CGFloat)] = [(0.0, 0.06), (0.18, 0.17), (0.34, 0.08), (0.52, 0.2), (0.7, 0.07), (0.88, 0.15), (1.0, 0.05)]

        // The lake, then the mountains' reflection in it.
        context.saveGState()
        context.clip(to: CGRect(x: 0, y: 0, width: w, height: horizon))
        gradient([(0.22, 0.2, 0.42), (0.55, 0.36, 0.48), (0.9, 0.55, 0.45)], from: .zero, to: CGPoint(x: 0, y: horizon))
        ridge(far, color: (0.36, 0.28, 0.5), mirrored: true)
        ridge(near, color: (0.2, 0.17, 0.34), mirrored: true)
        context.restoreGState()
        ridge(far, color: (0.36, 0.28, 0.5), mirrored: false)
        ridge(near, color: (0.2, 0.17, 0.34), mirrored: false)

        // Ripples across the water.
        context.setStrokeColor(CGColor(srgbRed: 1, green: 0.9, blue: 0.75, alpha: 0.35))
        context.setLineWidth(3)
        for index in 0..<7 {
            let y = horizon * (0.15 + CGFloat(index) * 0.11)
            let x = w * (0.58 + CGFloat(index % 3) * 0.03)
            context.move(to: CGPoint(x: x, y: y))
            context.addLine(to: CGPoint(x: x + w * (0.12 - CGFloat(index) * 0.01), y: y))
        }
        context.strokePath()
        return context.makeImage()
    }

    private static func writePhoto(to url: URL) throws {
        guard let image = photoImage() else { throw EditableImageError.unwritable(url) }
        try EditableImage.write(image, to: url, type: .jpeg, quality: 0.88)
    }

    // MARK: The report

    /// Three US Letter pages, each with a big number, so splitting and
    /// reordering show plainly.
    private static func writeReport(to url: URL) throws {
        var box = CGRect(x: 0, y: 0, width: 612, height: 792)
        guard let context = CGContext(url as CFURL, mediaBox: &box, [kCGPDFContextTitle: "Field Report"] as CFDictionary) else {
            throw EditableImageError.unwritable(url)
        }
        let pages: [(title: String, body: String, color: (CGFloat, CGFloat, CGFloat))] = [
            ("Field Report", "Notes from three days at Mountain Lake: the weather, the trail and what we saw. Try splitting this PDF into pages, or moving page 3 to the front.", (0.29, 0.44, 0.86)),
            ("The Trail", "Eleven kilometres around the lake, with one steep climb to the ridge. The view from the top is the photo in this folder.", (0.18, 0.62, 0.47)),
            ("The Weather", "Clear mornings, wind after noon, and a sunset every evening worth staying up for. The clip in this folder is one of them.", (0.86, 0.45, 0.33)),
        ]
        for (index, page) in pages.enumerated() {
            context.beginPDFPage(nil)
            context.setFillColor(CGColor(srgbRed: page.color.0, green: page.color.1, blue: page.color.2, alpha: 1))
            context.fill(CGRect(x: 0, y: 692, width: 612, height: 100))
            draw("\(index + 1)", in: CGRect(x: 54, y: 300, width: 504, height: 300), size: 260, bold: true, color: CGColor(srgbRed: page.color.0, green: page.color.1, blue: page.color.2, alpha: 0.18), context: context)
            draw(page.title, in: CGRect(x: 54, y: 712, width: 504, height: 60), size: 30, bold: true, color: CGColor(gray: 1, alpha: 1), context: context)
            draw(page.body, in: CGRect(x: 54, y: 470, width: 504, height: 200), size: 15, bold: false, color: CGColor(gray: 0.15, alpha: 1), context: context)
            draw("Page \(index + 1) of \(pages.count)", in: CGRect(x: 54, y: 40, width: 504, height: 20), size: 10, bold: false, color: CGColor(gray: 0.5, alpha: 1), context: context)
            context.endPDFPage()
        }
        context.closePDF()
    }

    private static func draw(_ text: String, in rect: CGRect, size: CGFloat, bold: Bool, color: CGColor, context: CGContext) {
        let font = CTFontCreateWithName((bold ? "HelveticaNeue-Bold" : "HelveticaNeue") as CFString, size, nil)
        let attributed = NSAttributedString(string: text, attributes: [
            NSAttributedString.Key(kCTFontAttributeName as String): font,
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): color,
        ])
        let framesetter = CTFramesetterCreateWithAttributedString(attributed)
        let frame = CTFramesetterCreateFrame(framesetter, CFRange(location: 0, length: 0), CGPath(rect: rect, transform: nil), nil)
        CTFrameDraw(frame, context)
    }

    // MARK: The clip

    /// Four seconds of a sun setting over water, as H.264.
    private static func writeClip(to url: URL) async throws {
        let width = 640
        let height = 360
        let frames = 120
        let writer = try AVAssetWriter(outputURL: url, fileType: .mov)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
        ])
        input.expectsMediaDataInRealTime = false
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input, sourcePixelBufferAttributes: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferWidthKey as String: width,
            kCVPixelBufferHeightKey as String: height,
        ])
        writer.add(input)
        guard writer.startWriting() else { throw writer.error ?? EditableImageError.unwritable(url) }
        writer.startSession(atSourceTime: .zero)

        for frame in 0..<frames {
            while !input.isReadyForMoreMediaData { try await Task.sleep(for: .milliseconds(5)) }
            guard let pool = adaptor.pixelBufferPool else { break }
            var buffer: CVPixelBuffer?
            CVPixelBufferPoolCreatePixelBuffer(nil, pool, &buffer)
            guard let buffer else { break }
            CVPixelBufferLockBaseAddress(buffer, [])
            if let context = CGContext(
                data: CVPixelBufferGetBaseAddress(buffer), width: width, height: height, bitsPerComponent: 8,
                bytesPerRow: CVPixelBufferGetBytesPerRow(buffer), space: CGColorSpace(name: CGColorSpace.sRGB)!,
                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
            ) {
                drawSunset(progress: CGFloat(frame) / CGFloat(frames - 1), in: context, size: CGSize(width: width, height: height))
            }
            CVPixelBufferUnlockBaseAddress(buffer, [])
            adaptor.append(buffer, withPresentationTime: CMTime(value: CMTimeValue(frame), timescale: 30))
        }
        input.markAsFinished()
        await writer.finishWriting()
        guard writer.status == .completed else { throw writer.error ?? EditableImageError.unwritable(url) }
    }

    private static func drawSunset(progress: CGFloat, in context: CGContext, size: CGSize) {
        let horizon = size.height * 0.4
        // The sky darkens as the sun goes down.
        let sky = CGColor(srgbRed: 0.98 - progress * 0.55, green: 0.6 - progress * 0.35, blue: 0.45 + progress * 0.1, alpha: 1)
        context.setFillColor(sky)
        context.fill(CGRect(x: 0, y: horizon, width: size.width, height: size.height - horizon))
        context.setFillColor(CGColor(srgbRed: 0.2, green: 0.22, blue: 0.42 - progress * 0.15, alpha: 1))
        context.fill(CGRect(x: 0, y: 0, width: size.width, height: horizon))
        // The sun sinks to the horizon, its reflection rising to meet it.
        let diameter = size.height * 0.22
        let sunY = horizon + size.height * 0.35 * (1 - progress) - diameter * 0.3
        context.setFillColor(CGColor(srgbRed: 1, green: 0.85 - progress * 0.2, blue: 0.55, alpha: 1))
        context.saveGState()
        context.clip(to: CGRect(x: 0, y: horizon, width: size.width, height: size.height))
        context.fillEllipse(in: CGRect(x: size.width * 0.5 - diameter / 2, y: sunY, width: diameter, height: diameter))
        context.restoreGState()
        context.setFillColor(CGColor(srgbRed: 1, green: 0.8, blue: 0.55, alpha: 0.35))
        let reflection = max(horizon - (sunY - horizon) - diameter, 0)
        context.fillEllipse(in: CGRect(x: size.width * 0.5 - diameter / 2, y: reflection, width: diameter, height: diameter * 0.6))
    }

    // MARK: The text

    static let notes = """
        # Mountain Lake

        Three days, one lake, and *far* too many photos.

        ## Packing list

        - Tent and sleeping bags
        - Stove, pot and **plenty** of coffee
        - Rain jackets, just in case

        ## The plan

        | Day | Walk | Distance |
        |-----|------|---------:|
        | 1 | Around the lake | 11 km |
        | 2 | Up to the ridge | 7 km |
        | 3 | Back to the car | 4 km |

        > The best view is from the ridge, just before sunset.

        Turn this file into a PDF with the Convert wheel: the headings, the list
        and the table come out properly typeset.
        """

    static let letter = """
        Dear Sam,

        We're back from Mountain Lake! The weather held, the trail was steep,
        and the sunsets were unreal. I've put the photos and a short clip in
        a folder for you.

        See you soon,
        Alex
        """
}
