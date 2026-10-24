import AppKit
import SwiftUI
import Testing
@testable import OpenShelf

/// The Add Background editor fits its window, so the swatches aren't clipped.
@MainActor
struct AddBackgroundLayoutTests {
    let actions = EditorActions(close: {}, finished: { _ in }, failed: { _ in }, focus: {})

    func image() throws -> EditableImage {
        let context = try #require(CGContext(
            data: nil, width: 64, height: 40, bitsPerComponent: 8, bytesPerRow: 0,
            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ))
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("openshelf-bg-\(UUID().uuidString).png")
        defer { try? FileManager.default.removeItem(at: url) }
        try EditableImage.write(try #require(context.makeImage()), to: url, type: .png)
        return try EditableImage.load(url)
    }

    /// Wider than the window means something can't shrink and gets clipped.
    @Test(arguments: [1, 3])
    func fitsItsWindow(count: Int) throws {
        let size = AddBackgroundEditor.windowSize(for: count)
        let editor = AddBackgroundEditor(images: Array(repeating: try image(), count: count), actions: actions)
        let width = NSHostingController(rootView: editor).sizeThatFits(in: CGSize(width: size.width, height: 3000)).width
        #expect(width <= size.width)
    }
}
