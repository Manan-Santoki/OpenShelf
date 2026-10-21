import CoreGraphics
import SwiftUI

/// Maps between image pixels (origin top-left, y down) and a preview showing the
/// image scaled to fit a container.
struct AnnotationPreviewFit {
    let imageSize: CGSize
    let containerSize: CGSize

    var scale: CGFloat {
        guard imageSize.width > 0, imageSize.height > 0 else { return 1 }
        return min(containerSize.width / imageSize.width, containerSize.height / imageSize.height)
    }

    var displaySize: CGSize {
        CGSize(width: imageSize.width * scale, height: imageSize.height * scale)
    }

    /// Converts a point in the displayed image to image pixels, clamped to the image.
    func imagePoint(fromView point: CGPoint) -> CGPoint {
        CGPoint(
            x: min(max(point.x / scale, 0), imageSize.width),
            y: min(max(point.y / scale, 0), imageSize.height)
        )
    }

    func viewPoint(fromImage point: CGPoint) -> CGPoint {
        CGPoint(x: point.x * scale, y: point.y * scale)
    }

    /// A distance of `points` on screen, expressed in image pixels.
    func imageLength(_ points: CGFloat) -> CGFloat {
        points / max(scale, 0.0001)
    }
}

enum AnnotationPreviewImage {
    /// A copy of `image` no larger than `maxPixelSize` on its long side, for
    /// responsive previews. Edits are still applied to the full-size original.
    static func downscaled(_ image: CGImage, maxPixelSize: Int = 1600) -> CGImage {
        let longSide = max(image.width, image.height)
        guard longSide > maxPixelSize else { return image }
        let factor = CGFloat(maxPixelSize) / CGFloat(longSide)
        let width = max(Int(CGFloat(image.width) * factor), 1)
        let height = max(Int(CGFloat(image.height) * factor), 1)
        guard let context = CGContext(
            data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB)!,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return image }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        return context.makeImage() ?? image
    }
}

/// An invisible button that gives an editor a keyboard shortcut, such as ⌫ to
/// delete the selection.
struct AnnotationShortcutButton: View {
    let key: KeyEquivalent
    var modifiers: EventModifiers = []
    var isEnabled = true
    let action: () -> Void

    var body: some View {
        Button("", action: action)
            .keyboardShortcut(key, modifiers: modifiers)
            .disabled(!isEnabled)
            .opacity(0)
            .frame(width: 0, height: 0)
            .accessibilityHidden(true)
    }
}

extension CGRect {
    /// The rectangle spanned by two opposite corners.
    init(annotationCorner a: CGPoint, _ b: CGPoint) {
        self.init(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }
}
