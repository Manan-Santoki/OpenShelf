import AppKit

/// Opens the editor for a Tools-wheel entry.
@MainActor
enum ToolLauncher {
    static func open(_ tool: ImageTool, with urls: [URL], near location: NSPoint = NSEvent.mouseLocation) {
        let images: [EditableImage]
        do {
            images = try urls.map(EditableImage.load)
        } catch {
            ToastController.shared.show(Toast(systemImage: "exclamationmark.triangle", title: "Couldn’t open image", subtitle: error.localizedDescription))
            return
        }
        guard let first = images.first else { return }
        if images.count > 1 && !tool.handlesBatches {
            ToastController.shared.show(Toast(systemImage: "info.circle", title: "\(tool.title) works on one image", subtitle: "Opened \(first.url.lastPathComponent)"))
        }

        switch tool {
        case .crop:
            EditorWindowController.present(size: CropEditor.windowSize, near: location) { CropEditor(image: first, actions: $0) }
        case .addBackground:
            EditorWindowController.present(size: AddBackgroundEditor.windowSize, near: location) { AddBackgroundEditor(image: first, actions: $0) }
        case .annotate:
            EditorWindowController.present(size: AnnotateEditor.windowSize, near: location) { AnnotateEditor(image: first, actions: $0) }
        case .redact:
            EditorWindowController.present(size: RedactEditor.windowSize, near: location) { RedactEditor(image: first, actions: $0) }
        case .edit:
            EditorWindowController.present(size: AdjustEditor.windowSize, near: location) { AdjustEditor(image: first, actions: $0) }
        case .compress:
            EditorWindowController.present(size: CompressEditor.windowSize, near: location) { CompressEditor(images: images, actions: $0) }
        case .metadata:
            EditorWindowController.present(size: MetadataEditor.windowSize, near: location) { MetadataEditor(images: images, actions: $0) }
        }
    }
}
