import AppKit
import CoreText
import SwiftUI

enum AnnotationTool: CaseIterable, Identifiable {
    case draw, arrow, box, highlight, text, callout

    var id: Self { self }

    var title: String {
        switch self {
        case .draw: "Draw"
        case .arrow: "Arrow"
        case .box: "Box"
        case .highlight: "Highlight"
        case .text: "Text"
        case .callout: "Callout"
        }
    }

    var systemImage: String {
        switch self {
        case .draw: "scribble"
        case .arrow: "arrow.up.right"
        case .box: "rectangle"
        case .highlight: "highlighter"
        case .text: "textformat"
        case .callout: "1.circle"
        }
    }

    var hint: String {
        switch self {
        case .draw: "Drag on the image to draw."
        case .arrow: "Drag from the tail toward the arrowhead."
        case .box: "Drag to draw a box."
        case .highlight: "Drag over the image to highlight."
        case .text: "Click where the text should go."
        case .callout: "Click to add numbered callouts."
        }
    }
}

/// One mark on the image. Coordinates are image pixels, origin top-left.
struct Annotation: Identifiable, Equatable {
    enum Figure: Equatable {
        case stroke([CGPoint])
        case arrow(from: CGPoint, to: CGPoint)
        case box(CGRect)
        case highlight(CGRect)
        case text(String, origin: CGPoint)
        /// Callouts keep the size they were created with, whatever the stroke width.
        case callout(number: Int, center: CGPoint, diameter: CGFloat)
    }

    let id = UUID()
    var figure: Figure
    var color: CGColor
    var lineWidth: CGFloat

    func offset(by delta: CGSize) -> Annotation {
        func move(_ point: CGPoint) -> CGPoint { CGPoint(x: point.x + delta.width, y: point.y + delta.height) }
        var moved = self
        switch figure {
        case .stroke(let points): moved.figure = .stroke(points.map(move))
        case .arrow(let from, let to): moved.figure = .arrow(from: move(from), to: move(to))
        case .box(let rect): moved.figure = .box(rect.offsetBy(dx: delta.width, dy: delta.height))
        case .highlight(let rect): moved.figure = .highlight(rect.offsetBy(dx: delta.width, dy: delta.height))
        case .text(let text, let origin): moved.figure = .text(text, origin: move(origin))
        case .callout(let number, let center, let diameter): moved.figure = .callout(number: number, center: move(center), diameter: diameter)
        }
        return moved
    }
}

/// Draws annotations with Core Graphics, both for the live preview and the saved
/// full-resolution image. Contexts must be y-down with image pixels as units.
enum AnnotationRenderer {
    static func fontSize(forLineWidth lineWidth: CGFloat) -> CGFloat {
        max(12, lineWidth * 4)
    }

    static func draw(_ annotation: Annotation, in context: CGContext) {
        context.saveGState()
        defer { context.restoreGState() }
        context.setStrokeColor(annotation.color)
        context.setFillColor(annotation.color)
        context.setLineWidth(annotation.lineWidth)
        context.setLineCap(.round)
        context.setLineJoin(.round)

        switch annotation.figure {
        case .stroke(let points):
            guard let first = points.first else { return }
            context.move(to: first)
            for point in points.dropFirst() { context.addLine(to: point) }
            if points.count == 1 { context.addLine(to: first) }
            context.strokePath()

        case .arrow(let from, let to):
            let angle = atan2(to.y - from.y, to.x - from.x)
            let head = max(annotation.lineWidth * 4, 12)
            let shaftEnd = CGPoint(x: to.x - cos(angle) * head * 0.7, y: to.y - sin(angle) * head * 0.7)
            context.move(to: from)
            context.addLine(to: shaftEnd)
            context.strokePath()
            context.move(to: to)
            context.addLine(to: CGPoint(x: to.x - cos(angle - 0.45) * head, y: to.y - sin(angle - 0.45) * head))
            context.addLine(to: CGPoint(x: to.x - cos(angle + 0.45) * head, y: to.y - sin(angle + 0.45) * head))
            context.closePath()
            context.fillPath()

        case .box(let rect):
            context.stroke(rect)

        case .highlight(let rect):
            context.setFillColor(annotation.color.copy(alpha: 0.35) ?? annotation.color)
            context.fill(rect)

        case .text(let text, let origin):
            let line = textLine(text, color: annotation.color, size: fontSize(forLineWidth: annotation.lineWidth))
            var ascent: CGFloat = 0
            CTLineGetTypographicBounds(line, &ascent, nil, nil)
            // Glyphs are drawn y-up; flip them back in this y-down context.
            context.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
            context.textPosition = CGPoint(x: origin.x, y: origin.y + ascent)
            CTLineDraw(line, context)

        case .callout(let number, let center, let diameter):
            let circle = CGRect(x: center.x - diameter / 2, y: center.y - diameter / 2, width: diameter, height: diameter)
            context.fillEllipse(in: circle)
            context.setStrokeColor(.white)
            context.setLineWidth(diameter * 0.07)
            context.strokeEllipse(in: circle.insetBy(dx: diameter * 0.035, dy: diameter * 0.035))
            let line = textLine("\(number)", color: .white, size: diameter * 0.55, weight: .bold)
            var ascent: CGFloat = 0
            var descent: CGFloat = 0
            let width = CTLineGetTypographicBounds(line, &ascent, &descent, nil)
            context.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
            context.textPosition = CGPoint(x: center.x - width / 2, y: center.y + (ascent - descent) / 2)
            CTLineDraw(line, context)
        }
    }

    /// The area an annotation covers, used for selection outlines.
    static func bounds(of annotation: Annotation) -> CGRect {
        let pad = annotation.lineWidth / 2
        switch annotation.figure {
        case .stroke(let points):
            return boundingRect(of: points).insetBy(dx: -pad, dy: -pad)
        case .arrow(let from, let to):
            let head = max(annotation.lineWidth * 4, 12)
            return CGRect(annotationCorner: from, to).insetBy(dx: -head / 2, dy: -head / 2)
        case .box(let rect):
            return rect.insetBy(dx: -pad, dy: -pad)
        case .highlight(let rect):
            return rect
        case .text(let text, let origin):
            let line = textLine(text, color: annotation.color, size: fontSize(forLineWidth: annotation.lineWidth))
            var ascent: CGFloat = 0
            var descent: CGFloat = 0
            let width = CTLineGetTypographicBounds(line, &ascent, &descent, nil)
            return CGRect(x: origin.x, y: origin.y, width: width, height: ascent + descent)
        case .callout(_, let center, let diameter):
            return CGRect(x: center.x - diameter / 2, y: center.y - diameter / 2, width: diameter, height: diameter)
        }
    }

    static func contains(_ annotation: Annotation, point: CGPoint, tolerance: CGFloat) -> Bool {
        let reach = annotation.lineWidth / 2 + tolerance
        switch annotation.figure {
        case .stroke(let points):
            guard let first = points.first else { return false }
            if points.count == 1 { return hypot(point.x - first.x, point.y - first.y) <= reach }
            return zip(points, points.dropFirst()).contains { distance(from: point, toSegment: $0, $1) <= reach }
        case .arrow(let from, let to):
            return distance(from: point, toSegment: from, to) <= max(reach, max(annotation.lineWidth * 2, 6))
        case .box, .highlight, .text:
            return bounds(of: annotation).insetBy(dx: -tolerance, dy: -tolerance).contains(point)
        case .callout(_, let center, let diameter):
            return hypot(point.x - center.x, point.y - center.y) <= diameter / 2 + tolerance
        }
    }

    /// Draws `annotations` over a full-resolution copy of `image`.
    static func render(_ annotations: [Annotation], over image: CGImage) -> CGImage? {
        let colorSpace = image.colorSpace.flatMap { $0.model == .rgb ? $0 : nil } ?? CGColorSpace(name: CGColorSpace.sRGB)!
        guard let context = CGContext(
            data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
            space: colorSpace, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        let height = CGFloat(image.height)
        context.draw(image, in: CGRect(x: 0, y: 0, width: CGFloat(image.width), height: height))
        context.translateBy(x: 0, y: height)
        context.scaleBy(x: 1, y: -1)
        for annotation in annotations {
            draw(annotation, in: context)
        }
        return context.makeImage()
    }

    private static func textLine(_ text: String, color: CGColor, size: CGFloat, weight: NSFont.Weight = .semibold) -> CTLine {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: size, weight: weight),
            NSAttributedString.Key(kCTForegroundColorAttributeName as String): color,
        ]
        return CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
    }

    private static func boundingRect(of points: [CGPoint]) -> CGRect {
        guard let first = points.first else { return .null }
        return points.dropFirst().reduce(CGRect(origin: first, size: .zero)) { $0.union(CGRect(origin: $1, size: .zero)) }
    }

    private static func distance(from point: CGPoint, toSegment a: CGPoint, _ b: CGPoint) -> CGFloat {
        let dx = b.x - a.x
        let dy = b.y - a.y
        let lengthSquared = dx * dx + dy * dy
        guard lengthSquared > 0 else { return hypot(point.x - a.x, point.y - a.y) }
        let t = min(max(((point.x - a.x) * dx + (point.y - a.y) * dy) / lengthSquared, 0), 1)
        return hypot(point.x - (a.x + t * dx), point.y - (a.y + t * dy))
    }
}

struct AnnotateEditor: View {
    static let windowSize = CGSize(width: 560, height: 660)

    let image: EditableImage
    let actions: EditorActions
    private let previewImage: CGImage

    @State private var annotations: [Annotation] = []
    @State private var history = EditHistory<[Annotation]>()
    @State private var draft: Annotation?
    @State private var selectedID: Annotation.ID?
    @State private var tool = AnnotationTool.draw
    @State private var color = NSColor.systemRed.cgColor
    @State private var lineWidth: Double
    @State private var textDraft: TextDraft?
    @State private var dragMode: DragMode?
    @FocusState private var isTextFieldFocused: Bool

    private struct TextDraft {
        var origin: CGPoint
        var text = ""
    }

    private enum DragMode {
        case move(Annotation, start: CGPoint)
        case create(start: CGPoint)
        case tap(CGPoint)
        case ignore
    }

    init(image: EditableImage, actions: EditorActions) {
        self.image = image
        self.actions = actions
        previewImage = AnnotationPreviewImage.downscaled(image.cgImage)
        let longSide = Double(max(image.cgImage.width, image.cgImage.height))
        _lineWidth = State(initialValue: min(max(longSide / 200, 2), 24))
    }

    var body: some View {
        EditorChrome(
            title: "Annotate",
            primaryTitle: "Save",
            isPrimaryEnabled: textDraft == nil && !annotations.isEmpty,
            // While typing, Esc finishes the text instead of closing the editor.
            closesOnEscape: textDraft == nil,
            actions: actions,
            onReset: reset,
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                preview
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.primary.opacity(0.05)))

                VStack(spacing: 6) {
                    HStack(spacing: 10) {
                        Picker("Tool", selection: $tool) {
                            ForEach(AnnotationTool.allCases) { tool in
                                Image(systemName: tool.systemImage)
                                    .help(tool.title)
                                    .accessibilityLabel(tool.title)
                                    .tag(tool)
                            }
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        .fixedSize()

                        UndoRedoButtons(
                            canUndo: history.canUndo && textDraft == nil,
                            canRedo: history.canRedo && textDraft == nil,
                            undo: undo,
                            redo: redo
                        )
                    }

                    Text(tool.hint)
                        .font(.system(size: 11))
                        .foregroundStyle(.secondary)
                }

                EditorRow(label: "Color") {
                    HStack(spacing: 8) {
                        ForEach(Array(Self.presetColors.enumerated()), id: \.offset) { _, preset in
                            ColorSwatch(color: preset, isSelected: preset == color) { color = preset }
                        }
                        ColorPicker("Custom color", selection: $color)
                            .labelsHidden()
                        Spacer(minLength: 0)
                    }
                }

                EditorSlider(label: "Width", value: $lineWidth, range: 2...24)
            }
            .background {
                AnnotationShortcutButton(key: .delete, isEnabled: selectedID != nil && textDraft == nil, action: deleteSelection)
                AnnotationShortcutButton(key: .deleteForward, isEnabled: selectedID != nil && textDraft == nil, action: deleteSelection)
            }
        }
        .onChange(of: tool) { _, _ in commitText() }
        .onChange(of: color) { _, newColor in updateSelection(coalescing: "color") { $0.color = newColor } }
        .onChange(of: lineWidth) { _, newWidth in updateSelection(coalescing: "width") { $0.lineWidth = CGFloat(newWidth) } }
        .onChange(of: selectedID) { _, _ in history.endCoalescing() }
    }

    private var preview: some View {
        GeometryReader { geometry in
            let fit = AnnotationPreviewFit(imageSize: image.pixelSize, containerSize: geometry.size)
            ZStack(alignment: .topLeading) {
                Image(decorative: previewImage, scale: 1)
                    .resizable()
                    .interpolation(.high)

                Canvas { context, _ in
                    context.withCGContext { cg in
                        cg.scaleBy(x: fit.scale, y: fit.scale)
                        for annotation in annotations {
                            AnnotationRenderer.draw(annotation, in: cg)
                        }
                        if let draft {
                            AnnotationRenderer.draw(draft, in: cg)
                        }
                        if let selected = annotations.first(where: { $0.id == selectedID }) {
                            let outline = AnnotationRenderer.bounds(of: selected)
                                .insetBy(dx: -fit.imageLength(4), dy: -fit.imageLength(4))
                            cg.setStrokeColor(NSColor.controlAccentColor.cgColor)
                            cg.setLineWidth(fit.imageLength(1.5))
                            cg.setLineDash(phase: 0, lengths: [fit.imageLength(4), fit.imageLength(3)])
                            cg.stroke(outline)
                        }
                    }
                }
                .allowsHitTesting(false)

                if let textDraft {
                    let origin = fit.viewPoint(fromImage: textDraft.origin)
                    TextField("Text", text: Binding(
                        get: { self.textDraft?.text ?? "" },
                        set: { self.textDraft?.text = $0 }
                    ))
                    .textFieldStyle(.plain)
                    .font(.system(size: AnnotationRenderer.fontSize(forLineWidth: lineWidth) * fit.scale, weight: .semibold))
                    .foregroundStyle(Color(cgColor: color))
                    .frame(width: max(160, fit.displaySize.width - origin.x - 8), alignment: .leading)
                    .padding(.horizontal, 3)
                    .background(RoundedRectangle(cornerRadius: 4).strokeBorder(Color.accentColor, style: StrokeStyle(lineWidth: 1, dash: [3, 2])))
                    .focused($isTextFieldFocused)
                    .onSubmit(commitText)
                    .onExitCommand(perform: commitText)
                    .onAppear { isTextFieldFocused = true }
                    .offset(x: origin.x - 3, y: origin.y)
                }
            }
            .frame(width: fit.displaySize.width, height: fit.displaySize.height)
            .contentShape(Rectangle())
            .gesture(drawingGesture(fit: fit))
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
        .padding(8)
    }

    private func drawingGesture(fit: AnnotationPreviewFit) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let point = fit.imagePoint(fromView: value.location)
                if dragMode == nil {
                    begin(at: fit.imagePoint(fromView: value.startLocation), fit: fit)
                }
                switch dragMode {
                case .move(let original, let start):
                    let moved = original.offset(by: CGSize(width: point.x - start.x, height: point.y - start.y))
                    if let index = annotations.firstIndex(where: { $0.id == original.id }) {
                        annotations[index] = moved
                    }
                case .create(let start):
                    extendDraft(from: start, to: point, fit: fit)
                case .tap, .ignore, nil:
                    break
                }
            }
            .onEnded { _ in
                switch dragMode {
                case .create:
                    if let draft, isMeaningful(draft, fit: fit) {
                        history.record(annotations)
                        annotations.append(draft)
                    }
                    draft = nil
                case .tap(let point):
                    place(at: point, fit: fit)
                case .move(let original, _):
                    // Record the move as one step, using the position it started from.
                    if let index = annotations.firstIndex(where: { $0.id == original.id }), annotations[index] != original {
                        var before = annotations
                        before[index] = original
                        history.record(before)
                    }
                case .ignore, nil:
                    break
                }
                dragMode = nil
            }
    }

    private func begin(at point: CGPoint, fit: AnnotationPreviewFit) {
        if textDraft != nil {
            // A click outside the text field finishes the text rather than starting something new.
            commitText()
            dragMode = .ignore
            return
        }
        let tolerance = fit.imageLength(6)
        if let hit = annotations.last(where: { AnnotationRenderer.contains($0, point: point, tolerance: tolerance) }) {
            selectedID = hit.id
            dragMode = .move(hit, start: point)
            return
        }
        selectedID = nil
        switch tool {
        case .text, .callout:
            dragMode = .tap(point)
        case .draw:
            draft = Annotation(figure: .stroke([point]), color: color, lineWidth: CGFloat(lineWidth))
            dragMode = .create(start: point)
        case .arrow:
            draft = Annotation(figure: .arrow(from: point, to: point), color: color, lineWidth: CGFloat(lineWidth))
            dragMode = .create(start: point)
        case .box:
            draft = Annotation(figure: .box(CGRect(origin: point, size: .zero)), color: color, lineWidth: CGFloat(lineWidth))
            dragMode = .create(start: point)
        case .highlight:
            draft = Annotation(figure: .highlight(CGRect(origin: point, size: .zero)), color: color, lineWidth: CGFloat(lineWidth))
            dragMode = .create(start: point)
        }
    }

    private func extendDraft(from start: CGPoint, to point: CGPoint, fit: AnnotationPreviewFit) {
        guard var draft else { return }
        switch draft.figure {
        case .stroke(var points):
            if let last = points.last, hypot(point.x - last.x, point.y - last.y) < fit.imageLength(1) { return }
            points.append(point)
            draft.figure = .stroke(points)
        case .arrow(let from, _):
            draft.figure = .arrow(from: from, to: point)
        case .box:
            draft.figure = .box(CGRect(annotationCorner: start, point))
        case .highlight:
            draft.figure = .highlight(CGRect(annotationCorner: start, point))
        case .text, .callout:
            break
        }
        self.draft = draft
    }

    private func isMeaningful(_ annotation: Annotation, fit: AnnotationPreviewFit) -> Bool {
        let minimum = fit.imageLength(4)
        switch annotation.figure {
        case .stroke:
            return true
        case .arrow(let from, let to):
            return hypot(to.x - from.x, to.y - from.y) >= minimum
        case .box(let rect), .highlight(let rect):
            return rect.width >= minimum && rect.height >= minimum
        case .text, .callout:
            return true
        }
    }

    private func place(at point: CGPoint, fit: AnnotationPreviewFit) {
        switch tool {
        case .text:
            textDraft = TextDraft(origin: point)
        case .callout:
            let next = annotations.compactMap { annotation -> Int? in
                if case .callout(let number, _, _) = annotation.figure { return number }
                return nil
            }.max().map { $0 + 1 } ?? 1
            let longSide = max(image.pixelSize.width, image.pixelSize.height)
            let diameter = max(24, longSide * 0.035)
            let callout = Annotation(figure: .callout(number: next, center: point, diameter: diameter), color: color, lineWidth: CGFloat(lineWidth))
            history.record(annotations)
            annotations.append(callout)
            selectedID = callout.id
        default:
            break
        }
    }

    private func commitText() {
        guard let textDraft else { return }
        self.textDraft = nil
        let text = textDraft.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return }
        let annotation = Annotation(figure: .text(text, origin: textDraft.origin), color: color, lineWidth: CGFloat(lineWidth))
        history.record(annotations)
        annotations.append(annotation)
        selectedID = annotation.id
    }

    /// Applies a colour or width change to the selected annotation. Continuous
    /// changes to the same property (dragging a slider) undo as one step.
    private func updateSelection(coalescing property: String, _ change: (inout Annotation) -> Void) {
        guard let selectedID, let index = annotations.firstIndex(where: { $0.id == selectedID }) else { return }
        var updated = annotations[index]
        change(&updated)
        guard updated != annotations[index] else { return }
        history.record(annotations, coalescingKey: "\(property)-\(selectedID)")
        annotations[index] = updated
    }

    private func deleteSelection() {
        guard annotations.contains(where: { $0.id == selectedID }) else { return }
        history.record(annotations)
        annotations.removeAll { $0.id == selectedID }
        selectedID = nil
    }

    private func undo() {
        guard textDraft == nil, let previous = history.undo(from: annotations) else { return }
        restore(previous)
    }

    private func redo() {
        guard textDraft == nil, let next = history.redo(from: annotations) else { return }
        restore(next)
    }

    private func restore(_ snapshot: [Annotation]) {
        annotations = snapshot
        draft = nil
        if !snapshot.contains(where: { $0.id == selectedID }) {
            selectedID = nil
        }
    }

    private func reset() {
        textDraft = nil
        draft = nil
        selectedID = nil
        guard !annotations.isEmpty else { return }
        history.record(annotations)
        annotations = []
    }

    private func save() {
        commitText()
        do {
            guard let rendered = AnnotationRenderer.render(annotations, over: image.cgImage) else {
                throw EditableImageError.unwritable(image.url)
            }
            let url = try image.save(rendered, suffix: "Annotated")
            actions.saved([url])
        } catch {
            actions.failed(error)
        }
    }

    private static var presetColors: [CGColor] {
        [NSColor.systemRed, .systemOrange, .systemYellow, .systemGreen, .systemBlue, .black, .white].map(\.cgColor)
    }
}

private struct ColorSwatch: View {
    let color: CGColor
    let isSelected: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Circle()
                .fill(Color(cgColor: color))
                .overlay(Circle().strokeBorder(Color.primary.opacity(0.2), lineWidth: 0.5))
                .frame(width: 18, height: 18)
                .padding(2)
                .overlay(Circle().strokeBorder(isSelected ? Color.accentColor : .clear, lineWidth: 2))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}
