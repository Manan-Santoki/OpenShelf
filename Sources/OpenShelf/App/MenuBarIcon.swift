import AppKit
import SwiftUI

/// The app icon's Monoline wheel as a menu-bar template image: outlined petals
/// with one filled, around an outlined hub. Template images adapt to light and
/// dark menu bars automatically.
enum MenuBarIcon {
    static let image: NSImage = {
        let size = NSSize(width: 18, height: 18)
        // Flipped so the shape's y-down geometry puts the lit petal at the top right.
        let image = NSImage(size: size, flipped: true) { rect in
            guard let context = NSGraphicsContext.current?.cgContext else { return false }
            let lineWidth: CGFloat = 1.1
            let segments = 6
            let sweep = 2 * CGFloat.pi / CGFloat(segments)
            context.setStrokeColor(NSColor.black.cgColor)
            context.setFillColor(NSColor.black.cgColor)
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
        image.isTemplate = true
        image.accessibilityDescription = "OpenShelf"
        return image
    }()
}
