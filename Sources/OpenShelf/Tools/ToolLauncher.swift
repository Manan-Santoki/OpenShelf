import AppKit

/// Runs a Tools-wheel entry: opens its editor, or runs it straight away for
/// one-click tools (Combine, Mute, Read QR).
@MainActor
enum ToolLauncher {
    static func open(_ tool: FileTool, with urls: [URL], near location: NSPoint = NSEvent.mouseLocation) {
        guard let first = urls.first else { return }
        let runner = ConversionRunner.shared

        switch tool {
        case .compress, .metadata, .edit, .annotate, .addBackground, .crop, .resize, .removeBackground, .redact:
            openImageEditor(tool, with: urls, near: location)

        case .readQR:
            Task {
                do {
                    let codes = try await QRReader.read(urls)
                    guard !codes.isEmpty else {
                        ToastController.shared.show(Toast(systemImage: "qrcode", title: "No QR code found"), near: location)
                        return
                    }
                    FileActions.copyText(codes.joined(separator: "\n"))
                    let title = codes.count == 1 ? "Copied QR code" : "Copied \(codes.count) QR codes"
                    ToastController.shared.show(Toast(systemImage: "qrcode", title: title, subtitle: codes[0]), near: location, for: .seconds(3))
                } catch {
                    ToastController.shared.show(Toast(systemImage: "exclamationmark.triangle", title: "Couldn’t read QR code", subtitle: error.localizedDescription), near: location)
                }
            }

        case .combinePDF:
            runner.run(title: "Combining into PDF", jobs: [urls], near: location) { sources, _ in
                [try await PDFTools.combine(sources)]
            }

        case .mute:
            runner.run(title: "Removing audio", jobs: urls.map { [$0] }, near: location) { sources, progress in
                [try await MediaTools.mute(sources[0], progress: progress)]
            }

        case .organizePages:
            EditorWindowController.present(size: PDFPagesEditor.windowSize, near: location) { PDFPagesEditor(url: first, actions: $0) }
        case .splitPDF:
            EditorWindowController.present(size: PDFSplitEditor.windowSize, near: location) { PDFSplitEditor(url: first, actions: $0) }
        case .compressPDF:
            EditorWindowController.present(size: PDFCompressEditor.windowSize, near: location) { PDFCompressEditor(urls: urls, actions: $0) }
        case .pdfMetadata:
            EditorWindowController.present(size: PDFMetadataEditor.windowSize, near: location) { PDFMetadataEditor(urls: urls, actions: $0) }

        case .trim:
            showOneFileNote(tool, urls: urls)
            EditorWindowController.present(size: MediaTrimEditor.windowSize, near: location) { MediaTrimEditor(url: first, actions: $0) }
        case .speed:
            EditorWindowController.present(size: MediaSpeedEditor.windowSize, near: location) { MediaSpeedEditor(urls: urls, actions: $0) }
        case .cropVideo:
            showOneFileNote(tool, urls: urls)
            EditorWindowController.present(size: VideoCropEditor.windowSize, near: location) { VideoCropEditor(url: first, actions: $0) }
        case .compressVideo:
            EditorWindowController.present(size: VideoCompressEditor.windowSize, near: location) { VideoCompressEditor(urls: urls, actions: $0) }
        case .mergeMedia:
            EditorWindowController.present(size: MediaMergeEditor.windowSize, near: location) { MediaMergeEditor(urls: urls, actions: $0) }
        case .splitMedia:
            showOneFileNote(tool, urls: urls)
            EditorWindowController.present(size: MediaSplitEditor.windowSize, near: location) { MediaSplitEditor(url: first, actions: $0) }
        case .rotateVideo:
            showOneFileNote(tool, urls: urls)
            EditorWindowController.present(size: VideoRotateEditor.windowSize, near: location) { VideoRotateEditor(url: first, actions: $0) }
        }
    }

    private static func openImageEditor(_ tool: FileTool, with urls: [URL], near location: NSPoint) {
        let images: [EditableImage]
        do {
            images = try urls.map(EditableImage.load)
        } catch {
            ToastController.shared.show(Toast(systemImage: "exclamationmark.triangle", title: "Couldn’t open image", subtitle: error.localizedDescription))
            return
        }
        guard !images.isEmpty else { return }

        switch tool {
        case .crop:
            EditorWindowController.present(size: CropEditor.windowSize(for: images.count), near: location) { CropEditor(images: images, actions: $0) }
        case .addBackground:
            EditorWindowController.present(size: AddBackgroundEditor.windowSize(for: images.count), near: location) { AddBackgroundEditor(images: images, actions: $0) }
        case .annotate:
            EditorWindowController.present(size: AnnotateEditor.windowSize(for: images.count), near: location) { AnnotateEditor(images: images, actions: $0) }
        case .redact:
            EditorWindowController.present(size: RedactEditor.windowSize(for: images.count), near: location) { RedactEditor(images: images, actions: $0) }
        case .edit:
            EditorWindowController.present(size: AdjustEditor.windowSize(for: images.count), near: location) { AdjustEditor(images: images, actions: $0) }
        case .compress:
            EditorWindowController.present(size: CompressEditor.windowSize, near: location) { CompressEditor(images: images, actions: $0) }
        case .metadata:
            EditorWindowController.present(size: MetadataEditor.windowSize, near: location) { MetadataEditor(images: images, actions: $0) }
        case .resize:
            EditorWindowController.present(size: ResizeEditor.windowSize, near: location) { ResizeEditor(images: images, actions: $0) }
        case .removeBackground:
            EditorWindowController.present(size: RemoveBackgroundEditor.windowSize(for: images.count), near: location) { RemoveBackgroundEditor(images: images, actions: $0) }
        default:
            break
        }
    }

    /// Editors that work on a single file open the first one and say so.
    private static func showOneFileNote(_ tool: FileTool, urls: [URL]) {
        guard urls.count > 1 else { return }
        ToastController.shared.show(Toast(systemImage: "info.circle", title: "\(tool.title) works on one file", subtitle: "Opened \(urls[0].lastPathComponent)"))
    }
}
