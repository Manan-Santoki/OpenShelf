import AppKit
import CoreImage
import SwiftUI

enum RedactionStyle: CaseIterable, Identifiable {
    case box, pixelate, blur

    var id: Self { self }

    var title: String {
        switch self {
        case .box: "Black box"
        case .pixelate: "Pixelate"
        case .blur: "Blur"
        }
    }
}

struct RedactRegion: Identifiable, Equatable {
    let id = UUID()
    /// Image pixels, origin top-left.
    var rect: CGRect
}

/// Applies redactions with Core Image.
@MainActor
enum Redaction {
    private static let context = CIContext()

    /// Returns `image` with `regions` (image pixels, origin top-left) hidden.
    /// Strength (0–100) is relative to the image's size, so a downscaled preview
    /// looks the same as the full-resolution result.
    static func apply(to image: CGImage, regions: [CGRect], style: RedactionStyle, strength: Double) -> CGImage? {
        guard !regions.isEmpty else { return image }
        let source = CIImage(cgImage: image)
        let extent = source.extent
        let longSide = max(extent.width, extent.height)
        let amount = min(max(strength, 0), 100) / 100

        // Filter the whole image once, then reveal it only inside the regions.
        // Clamping first keeps edges from fading to transparent.
        let covered: CIImage
        switch style {
        case .box:
            covered = CIImage(color: .black).cropped(to: extent)
        case .pixelate:
            covered = source.clampedToExtent()
                .applyingFilter("CIPixellate", parameters: [
                    kCIInputScaleKey: max(longSide * (0.006 + amount * 0.044), 2),
                    kCIInputCenterKey: CIVector(x: 0, y: 0),
                ])
                .cropped(to: extent)
        case .blur:
            covered = source.clampedToExtent()
                .applyingGaussianBlur(sigma: max(longSide * (0.003 + amount * 0.025), 1))
                .cropped(to: extent)
        }

        var mask = CIImage(color: .black).cropped(to: extent)
        for region in regions {
            // Core Image is y-up.
            let flipped = CGRect(x: region.minX, y: extent.height - region.maxY, width: region.width, height: region.height)
            mask = CIImage(color: .white).cropped(to: flipped).composited(over: mask)
        }
        let output = covered.applyingFilter("CIBlendWithMask", parameters: [
            kCIInputBackgroundImageKey: source,
            kCIInputMaskImageKey: mask,
        ])
        return context.createCGImage(output, from: extent)
    }
}

struct RedactEditor: View {
    static let windowSize = CGSize(width: 560, height: 640)

    let image: EditableImage
    let actions: EditorActions
    private let previewImage: CGImage

    @State private var regions: [RedactRegion] = []
    @State private var draftRect: CGRect?
    @State private var selectedID: RedactRegion.ID?
    @State private var hoveredID: RedactRegion.ID?
    @State private var style = RedactionStyle.box
    @State private var strength = 50.0
    @State private var dragMode: DragMode?
    @State private var renderedPreview: CGImage?

    private enum DragMode {
        case move(RedactRegion, start: CGPoint)
        case create(start: CGPoint)
    }

    private struct PreviewKey: Equatable {
        var rects: [CGRect]
        var style: RedactionStyle
        var strength: Double
    }

    init(image: EditableImage, actions: EditorActions) {
        self.image = image
        self.actions = actions
        previewImage = AnnotationPreviewImage.downscaled(image.cgImage)
    }

    var body: some View {
        EditorChrome(
            title: "Redact",
            primaryTitle: "Save",
            isPrimaryEnabled: !regions.isEmpty,
            actions: actions,
            onReset: reset,
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                preview
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.05)))

                Text(hint)
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)

                EditorRow(label: "Style") {
                    Picker("Style", selection: $style) {
                        ForEach(RedactionStyle.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }

                EditorSlider(label: "Strength", value: $strength, range: 1...100, unit: "%")
                    .disabled(style == .box)
                    .opacity(style == .box ? 0.45 : 1)
            }
            .background {
                AnnotationShortcutButton(key: .delete, isEnabled: selectedID != nil, action: deleteSelection)
                AnnotationShortcutButton(key: .deleteForward, isEnabled: selectedID != nil, action: deleteSelection)
            }
        }
        .onChange(of: previewKey, initial: true) { _, key in
            renderPreview(for: key)
        }
    }

    private var hint: String {
        switch regions.count {
        case 0: "Drag over anything you want to hide."
        case 1: "1 area · drag to move, ⌫ to remove"
        case let count: "\(count) areas · drag to move, ⌫ to remove"
        }
    }

    private var previewKey: PreviewKey {
        PreviewKey(rects: regions.map(\.rect) + (draftRect.map { [$0] } ?? []), style: style, strength: strength)
    }

    private var preview: some View {
        GeometryReader { geometry in
            let fit = AnnotationPreviewFit(imageSize: image.pixelSize, containerSize: geometry.size)
            ZStack(alignment: .topLeading) {
                Image(decorative: renderedPreview ?? previewImage, scale: 1)
                    .resizable()
                    .interpolation(.high)

                Canvas { context, _ in
                    for region in regions {
                        let rect = viewRect(region.rect, fit: fit)
                        let isSelected = region.id == selectedID
                        context.stroke(
                            Path(rect),
                            with: .color(isSelected ? .accentColor : .white.opacity(0.85)),
                            style: StrokeStyle(lineWidth: isSelected ? 2 : 1, dash: isSelected ? [] : [4, 3])
                        )
                    }
                    if let draftRect {
                        context.stroke(Path(viewRect(draftRect, fit: fit)), with: .color(.accentColor), style: StrokeStyle(lineWidth: 1.5, dash: [4, 3]))
                    }
                }
                .allowsHitTesting(false)

                if let id = hoveredID ?? selectedID, let region = regions.first(where: { $0.id == id }), dragMode == nil {
                    let rect = viewRect(region.rect, fit: fit)
                    Button {
                        regions.removeAll { $0.id == id }
                        if selectedID == id { selectedID = nil }
                        hoveredID = nil
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(.system(size: 16))
                            .symbolRenderingMode(.palette)
                            .foregroundStyle(.white, Color.accentColor)
                            .shadow(color: .black.opacity(0.25), radius: 2, y: 1)
                    }
                    .buttonStyle(.plain)
                    .help("Remove this area")
                    .accessibilityLabel("Remove area")
                    .offset(x: rect.maxX - 9, y: rect.minY - 9)
                }
            }
            .frame(width: fit.displaySize.width, height: fit.displaySize.height)
            .contentShape(Rectangle())
            .gesture(regionGesture(fit: fit))
            .onContinuousHover { phase in
                switch phase {
                case .active(let location):
                    let point = fit.imagePoint(fromView: location)
                    hoveredID = regions.last { $0.rect.insetBy(dx: -fit.imageLength(10), dy: -fit.imageLength(10)).contains(point) }?.id
                case .ended:
                    hoveredID = nil
                }
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
        .padding(8)
    }

    private func regionGesture(fit: AnnotationPreviewFit) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let point = fit.imagePoint(fromView: value.location)
                if dragMode == nil {
                    let start = fit.imagePoint(fromView: value.startLocation)
                    if let hit = regions.last(where: { $0.rect.contains(start) }) {
                        selectedID = hit.id
                        dragMode = .move(hit, start: start)
                    } else {
                        selectedID = nil
                        dragMode = .create(start: start)
                    }
                }
                switch dragMode {
                case .move(let original, let start):
                    guard let index = regions.firstIndex(where: { $0.id == original.id }) else { return }
                    var rect = original.rect.offsetBy(dx: point.x - start.x, dy: point.y - start.y)
                    rect.origin.x = min(max(rect.minX, 0), image.pixelSize.width - rect.width)
                    rect.origin.y = min(max(rect.minY, 0), image.pixelSize.height - rect.height)
                    regions[index].rect = rect
                case .create(let start):
                    draftRect = CGRect(annotationCorner: start, point)
                case nil:
                    break
                }
            }
            .onEnded { _ in
                if case .create = dragMode, let draftRect,
                   draftRect.width >= fit.imageLength(4), draftRect.height >= fit.imageLength(4) {
                    let region = RedactRegion(rect: draftRect)
                    regions.append(region)
                    selectedID = region.id
                }
                draftRect = nil
                dragMode = nil
            }
    }

    private func viewRect(_ rect: CGRect, fit: AnnotationPreviewFit) -> CGRect {
        CGRect(origin: fit.viewPoint(fromImage: rect.origin), size: CGSize(width: rect.width * fit.scale, height: rect.height * fit.scale))
    }

    private func renderPreview(for key: PreviewKey) {
        let factor = CGFloat(previewImage.width) / CGFloat(max(image.cgImage.width, 1))
        let scaled = key.rects.map { CGRect(x: $0.minX * factor, y: $0.minY * factor, width: $0.width * factor, height: $0.height * factor) }
        renderedPreview = scaled.isEmpty ? nil : Redaction.apply(to: previewImage, regions: scaled, style: key.style, strength: key.strength)
    }

    private func deleteSelection() {
        regions.removeAll { $0.id == selectedID }
        selectedID = nil
    }

    private func reset() {
        regions = []
        draftRect = nil
        selectedID = nil
        hoveredID = nil
    }

    private func save() {
        do {
            guard let output = Redaction.apply(to: image.cgImage, regions: regions.map(\.rect), style: style, strength: strength) else {
                throw EditableImageError.unwritable(image.url)
            }
            // Redacted copies shouldn't carry location or camera details either.
            let url = try image.save(output, suffix: "Redacted", keepsMetadata: false)
            actions.saved([url])
        } catch {
            actions.failed(error)
        }
    }
}
