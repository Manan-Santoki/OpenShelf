import AppKit
import SwiftUI

/// The app icon's Monoline wheel as a menu-bar image: outlined petals with one
/// filled, around an outlined hub.
enum MenuBarIcon {
    /// A template image, which adapts to light and dark menu bars automatically.
    static let image = wheel(color: nil)

    /// The wheel in blue, shown while an update is waiting to be installed.
    static let updateAvailableImage = wheel(color: .systemBlue)

    /// The wheel drawn in `color`, or as a template image when `color` is nil.
    private static func wheel(color: NSColor?) -> NSImage {
        let size = NSSize(width: 18, height: 18)
        // Flipped so the shape's y-down geometry puts the lit petal at the top right.
        let image = NSImage(size: size, flipped: true) { rect in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            let lineWidth: CGFloat = 1.1
            let segments = 6
            let sweep = 2 * CGFloat.pi / CGFloat(segments)
            let ink = (color ?? .black).cgColor
            context.setStrokeColor(ink)
            context.setFillColor(ink)
            context.setLineWidth(lineWidth)

            for index in 0..<segments {
                let center = sweep * CGFloat(index)
                let petal = RoundedAnnularSector(
                    startAngle: center - sweep / 2,
                    endAngle: center + sweep / 2,
                    innerRadius: 4.1,
                    outerRadius: 8.5,
                    gap: 1.4,
                    cornerRadius: 1.4
                )
                context.addPath(petal.path(in: rect).cgPath)
                if index == 1 {
                    context.fillPath()
                } else {
                    context.strokePath()
                }
            }

            let hubRadius: CGFloat = 2.6
            context.strokeEllipse(in: CGRect(x: rect.midX - hubRadius, y: rect.midY - hubRadius, width: hubRadius * 2, height: hubRadius * 2))
            return true
        }
        image.isTemplate = color == nil
        image.accessibilityDescription = color == nil ? "OpenShelf" : "OpenShelf, update available"
        return image
    }
}
