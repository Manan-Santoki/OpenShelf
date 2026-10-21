// Renders the background of the installer disk image to Support/dmg-background.png
// (and @2x). The app icon sits at (150, 190) and Applications at (450, 190).
// Usage: swift scripts/make-dmg-background.swift  (run from the repository root)
import AppKit
import SwiftUI

struct Background: View {
    var body: some View {
        ZStack {
            LinearGradient(colors: [Color(white: 0.13), Color(white: 0.06)], startPoint: .top, endPoint: .bottom)

            VStack(spacing: 6) {
                Text("Install OpenShelf")
                    .font(.system(size: 22, weight: .bold))
                Text("Drag the app onto the Applications folder")
                    .font(.system(size: 13))
                    .foregroundStyle(.white.opacity(0.6))
            }
            .foregroundStyle(.white)
            .position(x: 300, y: 52)

            // Arrow between the two icons.
            Image(systemName: "arrow.right")
                .font(.system(size: 34, weight: .semibold))
                .foregroundStyle(.white.opacity(0.55))
                .position(x: 300, y: 190)

            VStack(alignment: .leading, spacing: 4) {
                Text("First launch")
                    .font(.system(size: 12, weight: .semibold))
                Text("If macOS says it can't verify OpenShelf, open System Settings → Privacy & Security, scroll down and click Open Anyway. You only need to do this once.")
                    .font(.system(size: 11))
                    .foregroundStyle(.white.opacity(0.6))
                    .fixedSize(horizontal: false, vertical: true)
            }
            .foregroundStyle(.white)
            .padding(12)
            .frame(width: 520, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.white.opacity(0.06)))
            .position(x: 300, y: 335)
        }
        .frame(width: 600, height: 400)
    }
}

@MainActor
func write(scale: CGFloat, to path: String) throws {
    let renderer = ImageRenderer(content: Background())
    renderer.scale = scale
    guard let image = renderer.cgImage else { fatalError("render failed") }
    try NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
}

try MainActor.assumeIsolated {
    try write(scale: 1, to: "Support/dmg-background.png")
    try write(scale: 2, to: "Support/dmg-background@2x.png")
    print("Wrote Support/dmg-background.png")
}
