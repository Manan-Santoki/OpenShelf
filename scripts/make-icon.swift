// Renders the app icon and writes Support/AppIcon.icns.
// Usage: swift scripts/make-icon.swift  (run from the repository root)
import AppKit
import SwiftUI

/// Same geometry as the wheel's wedges (Sources/OpenShelf/Wheel/WheelView.swift).
/// Angles are in radians, clockwise from 12 o'clock.
struct RoundedAnnularSector: Shape {
    var startAngle: CGFloat
    var endAngle: CGFloat
    var innerRadius: CGFloat
    var outerRadius: CGFloat
    var gap: CGFloat
    var cornerRadius: CGFloat

    func path(in rect: CGRect) -> Path {
        let center = CGPoint(x: rect.midX, y: rect.midY)
        let halfGap = gap / 2
        let radius = min(cornerRadius, (outerRadius - innerRadius) / 2)
        func point(_ distance: CGFloat, _ angle: CGFloat) -> CGPoint {
            CGPoint(x: center.x + distance * sin(angle), y: center.y - distance * cos(angle))
        }
        func startSide(_ distance: CGFloat) -> CGFloat { startAngle + asin(min(halfGap / distance, 1)) }
        func endSide(_ distance: CGFloat) -> CGFloat { endAngle - asin(min(halfGap / distance, 1)) }
        func arcAngle(_ angle: CGFloat) -> Angle { .radians(Double(angle) - .pi / 2) }
        let outerInset = min(radius / outerRadius, (endSide(outerRadius) - startSide(outerRadius)) / 2)
        let innerInset = min(radius / innerRadius, (endSide(innerRadius) - startSide(innerRadius)) / 2)

        var path = Path()
        path.move(to: point(outerRadius - radius, startSide(outerRadius - radius)))
        path.addQuadCurve(to: point(outerRadius, startSide(outerRadius) + outerInset), control: point(outerRadius, startSide(outerRadius)))
        path.addArc(center: center, radius: outerRadius, startAngle: arcAngle(startSide(outerRadius) + outerInset), endAngle: arcAngle(endSide(outerRadius) - outerInset), clockwise: false)
        path.addQuadCurve(to: point(outerRadius - radius, endSide(outerRadius - radius)), control: point(outerRadius, endSide(outerRadius)))
        path.addLine(to: point(innerRadius + radius, endSide(innerRadius + radius)))
        path.addQuadCurve(to: point(innerRadius, endSide(innerRadius) - innerInset), control: point(innerRadius, endSide(innerRadius)))
        path.addArc(center: center, radius: innerRadius, startAngle: arcAngle(endSide(innerRadius) - innerInset), endAngle: arcAngle(startSide(innerRadius) + innerInset), clockwise: true)
        path.addQuadCurve(to: point(innerRadius + radius, startSide(innerRadius + radius)), control: point(innerRadius, startSide(innerRadius)))
        path.closeSubpath()
        return path
    }
}

/// Monoline: an outline wheel on black with one solid petal lit, as when a
/// wedge is hovered.
struct IconView: View {
    private let segments = 6

    var body: some View {
        ZStack {
            // Body inset to Apple's icon grid: 824 pt on a 1024 pt canvas.
            RoundedRectangle(cornerRadius: 185, style: .continuous)
                .fill(LinearGradient(colors: [Color(white: 0.125), Color(white: 0.02)], startPoint: .top, endPoint: .bottom))
                .overlay {
                    RoundedRectangle(cornerRadius: 185, style: .continuous)
                        .fill(LinearGradient(colors: [.white.opacity(0.06), .clear], startPoint: .top, endPoint: .center))
                }
                .frame(width: 824, height: 824)
                .shadow(color: .black.opacity(0.3), radius: 18, y: 10)

            ForEach(0..<segments, id: \.self) { index in
                let sweep = 2 * CGFloat.pi / CGFloat(segments)
                let center = sweep * CGFloat(index)
                let petal = RoundedAnnularSector(
                    startAngle: center - sweep / 2,
                    endAngle: center + sweep / 2,
                    innerRadius: 130,
                    outerRadius: 300,
                    gap: 26,
                    cornerRadius: 46
                )
                if index == 1 {
                    petal.fill(.white)
                } else {
                    petal.stroke(.white.opacity(0.85), lineWidth: 10)
                }
            }

            Circle()
                .stroke(.white.opacity(0.85), lineWidth: 10)
                .frame(width: 200, height: 200)
        }
        .frame(width: 1024, height: 1024)
    }
}

@MainActor
func render(size: Int) -> Data {
    let renderer = ImageRenderer(content: IconView().scaleEffect(CGFloat(size) / 1024).frame(width: CGFloat(size), height: CGFloat(size)))
    renderer.scale = 1
    renderer.isOpaque = false
    guard let image = renderer.cgImage else { fatalError("render failed") }
    let rep = NSBitmapImageRep(cgImage: image)
    return rep.representation(using: .png, properties: [:])!
}

@MainActor
func main() throws {
    let iconset = URL(fileURLWithPath: NSTemporaryDirectory()).appendingPathComponent("AppIcon.iconset")
    try? FileManager.default.removeItem(at: iconset)
    try FileManager.default.createDirectory(at: iconset, withIntermediateDirectories: true)
    for points in [16, 32, 128, 256, 512] {
        try render(size: points).write(to: iconset.appendingPathComponent("icon_\(points)x\(points).png"))
        try render(size: points * 2).write(to: iconset.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
    }
    try render(size: 1024).write(to: URL(fileURLWithPath: "Support/AppIcon.png"))

    let iconutil = Process()
    iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
    iconutil.arguments = ["-c", "icns", iconset.path, "-o", "Support/AppIcon.icns"]
    try iconutil.run()
    iconutil.waitUntilExit()
    print(iconutil.terminationStatus == 0 ? "Wrote Support/AppIcon.icns" : "iconutil failed")
}

try MainActor.assumeIsolated { try main() }
