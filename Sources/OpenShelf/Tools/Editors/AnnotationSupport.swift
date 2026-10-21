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

/// Snapshot-based undo and redo for editors whose state is a small value, such
/// as a list of annotations.
struct EditHistory<Snapshot> {
    private(set) var undoStack: [Snapshot] = []
    private(set) var redoStack: [Snapshot] = []
    private var coalescingKey: AnyHashable?
    let limit: Int

    init(limit: Int = 100) {
        self.limit = limit
    }

    var canUndo: Bool { !undoStack.isEmpty }
    var canRedo: Bool { !redoStack.isEmpty }

    /// Call with the state as it was just before a change. Consecutive changes
    /// with the same non-nil `coalescingKey` (e.g. dragging a width slider) undo
    /// as one step.
    mutating func record(_ snapshot: Snapshot, coalescingKey key: AnyHashable? = nil) {
        if let key, key == coalescingKey { return }
        coalescingKey = key
        undoStack.append(snapshot)
        if undoStack.count > limit {
            undoStack.removeFirst(undoStack.count - limit)
        }
        redoStack.removeAll()
    }

    /// Ends any run of coalesced changes, so the next change gets its own step.
    mutating func endCoalescing() {
        coalescingKey = nil
    }

    /// Returns the state to restore, given the current one.
    mutating func undo(from current: Snapshot) -> Snapshot? {
        coalescingKey = nil
        guard let previous = undoStack.popLast() else { return nil }
        redoStack.append(current)
        return previous
    }

    mutating func redo(from current: Snapshot) -> Snapshot? {
        coalescingKey = nil
        guard let next = redoStack.popLast() else { return nil }
        undoStack.append(current)
        return next
    }
}

/// Undo and redo buttons with ⌘Z and ⇧⌘Z.
struct UndoRedoButtons: View {
    let canUndo: Bool
    let canRedo: Bool
    let undo: () -> Void
    let redo: () -> Void

    var body: some View {
        HStack(spacing: 2) {
            Button(action: undo) {
                Image(systemName: "arrow.uturn.backward")
            }
            .keyboardShortcut("z", modifiers: .command)
            .disabled(!canUndo)
            .help("Undo (⌘Z)")
            .accessibilityLabel("Undo")

            Button(action: redo) {
                Image(systemName: "arrow.uturn.forward")
            }
            .keyboardShortcut("z", modifiers: [.command, .shift])
            .disabled(!canRedo)
            .help("Redo (⇧⌘Z)")
            .accessibilityLabel("Redo")
        }
        .buttonStyle(.borderless)
        .font(.system(size: 13, weight: .medium))
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
