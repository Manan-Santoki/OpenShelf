import SwiftUI

/// Crops an image to a rectangle chosen with handles, an aspect preset, or exact
/// pixel dimensions.
struct CropEditor: View {
    static let windowSize = CGSize(width: 440, height: 600)

    let image: EditableImage
    let actions: EditorActions

    /// The crop in image pixels, origin at the top left.
    @State private var crop: CGRect
    @State private var ratio = CropRatio.free
    @State private var dragStart: CGRect?

    private let minimumSize: CGFloat = 16

    init(image: EditableImage, actions: EditorActions) {
        self.image = image
        self.actions = actions
        _crop = State(initialValue: CGRect(origin: .zero, size: image.pixelSize))
    }

    private var bounds: CGRect { CGRect(origin: .zero, size: image.pixelSize) }

    var body: some View {
        EditorChrome(
            title: "Crop Image",
            primaryTitle: "Apply",
            // Cropping to the whole image would only save a copy of it.
            isPrimaryEnabled: crop != bounds,
            actions: actions,
            onReset: reset,
            onPrimary: apply
        ) {
            VStack(spacing: 14) {
                GeometryReader { proxy in
                    canvas(in: proxy.size)
                }
                .frame(height: 400)

                EditorRow(label: "Aspect ratio") {
                    Picker("Aspect ratio", selection: $ratio) {
                        ForEach(CropRatio.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .onChange(of: ratio) { _, newRatio in snap(to: newRatio) }
                }

                EditorRow(label: "Size") {
                    HStack(spacing: 6) {
                        Text("W").foregroundStyle(.secondary)
                        TextField("Width", value: dimensionBinding(\.width), format: .number.grouping(.never))
                            .frame(width: 72)
                        Text("H").foregroundStyle(.secondary)
                        TextField("Height", value: dimensionBinding(\.height), format: .number.grouping(.never))
                            .frame(width: 72)
                        Text("px").foregroundStyle(.secondary)
                        Spacer(minLength: 0)
                    }
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 12))
                }
            }
        }
    }

    // MARK: Canvas

    private func canvas(in available: CGSize) -> some View {
        let scale = min(available.width / bounds.width, available.height / bounds.height)
        let fitted = CGSize(width: bounds.width * scale, height: bounds.height * scale)
        let cropInView = CGRect(x: crop.minX * scale, y: crop.minY * scale, width: crop.width * scale, height: crop.height * scale)

        return ZStack(alignment: .topLeading) {
            Image(decorative: image.cgImage, scale: 1)
                .resizable()
                .interpolation(.high)
                .frame(width: fitted.width, height: fitted.height)

            // Dim everything outside the crop.
            Path { path in
                path.addRect(CGRect(origin: .zero, size: fitted))
                path.addRect(cropInView)
            }
            .fill(Color.black.opacity(0.5), style: FillStyle(eoFill: true))
            .allowsHitTesting(false)

            Path(cropInView)
                .stroke(Color.accentColor, lineWidth: 2)
                .allowsHitTesting(false)

            Rectangle()
                .fill(Color.white.opacity(0.001))
                .frame(width: cropInView.width, height: cropInView.height)
                .position(x: cropInView.midX, y: cropInView.midY)
                .gesture(dragGesture(for: nil, scale: scale))
                .accessibilityLabel("Crop area")

            ForEach(CropHandle.allCases) { handle in
                HandleView(handle: handle)
                    .position(handle.position(in: cropInView))
                    .gesture(dragGesture(for: handle, scale: scale))
            }
        }
        .frame(width: fitted.width, height: fitted.height)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func dragGesture(for handle: CropHandle?, scale: CGFloat) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                let start = dragStart ?? crop
                if dragStart == nil { dragStart = crop }
                let delta = CGSize(width: value.translation.width / scale, height: value.translation.height / scale)
                crop = CropMath.adjusted(start, handle: handle, delta: delta, ratio: ratio.value, bounds: bounds, minimumSize: minimumSize)
            }
            .onEnded { _ in dragStart = nil }
    }

    // MARK: Editing

    private func dimensionBinding(_ keyPath: KeyPath<CGRect, CGFloat>) -> Binding<Int> {
        Binding {
            Int(crop[keyPath: keyPath].rounded())
        } set: { newValue in
            let isWidth = keyPath == \CGRect.width
            crop = CropMath.resized(
                crop,
                width: isWidth ? newValue : nil,
                height: isWidth ? nil : newValue,
                ratio: ratio.value,
                bounds: bounds,
                minimumSize: minimumSize
            )
        }
    }

    private func snap(to ratio: CropRatio) {
        guard let value = ratio.value else { return }
        crop = CropMath.largest(ratio: value, centeredAt: CGPoint(x: crop.midX, y: crop.midY), in: bounds)
    }

    private func reset() {
        ratio = .free
        crop = bounds
    }

    private func apply() {
        // The crop is kept on whole pixels, so this is exactly the size shown.
        guard let cropped = image.cgImage.cropping(to: CropMath.snapped(crop, in: bounds)) else {
            actions.failed(EditableImageError.unwritable(image.url))
            return
        }
        do {
            actions.saved([try image.save(cropped, suffix: "Cropped")])
        } catch {
            actions.failed(error)
        }
    }
}

enum CropRatio: CaseIterable, Identifiable {
    case free, square, landscape, tall, standard, portrait

    var id: Self { self }

    var title: String {
        switch self {
        case .free: "Free"
        case .square: "1:1"
        case .landscape: "16:9"
        case .tall: "9:16"
        case .standard: "4:3"
        case .portrait: "3:4"
        }
    }

    /// Width divided by height, or nil for a free crop.
    var value: CGFloat? {
        switch self {
        case .free: nil
        case .square: 1
        case .landscape: 16 / 9
        case .tall: 9 / 16
        case .standard: 4 / 3
        case .portrait: 3 / 4
        }
    }
}

enum CropHandle: CaseIterable, Identifiable {
    case topLeft, top, topRight, right, bottomRight, bottom, bottomLeft, left

    var id: Self { self }

    var movesLeft: Bool { [.topLeft, .left, .bottomLeft].contains(self) }
    var movesRight: Bool { [.topRight, .right, .bottomRight].contains(self) }
    var movesTop: Bool { [.topLeft, .top, .topRight].contains(self) }
    var movesBottom: Bool { [.bottomLeft, .bottom, .bottomRight].contains(self) }
    var isCorner: Bool { (movesLeft || movesRight) && (movesTop || movesBottom) }

    func position(in rect: CGRect) -> CGPoint {
        let x = movesLeft ? rect.minX : movesRight ? rect.maxX : rect.midX
        let y = movesTop ? rect.minY : movesBottom ? rect.maxY : rect.midY
        return CGPoint(x: x, y: y)
    }
}

private struct HandleView: View {
    let handle: CropHandle

    var body: some View {
        let size: CGSize = if handle.isCorner {
            CGSize(width: 12, height: 12)
        } else if handle.movesTop || handle.movesBottom {
            CGSize(width: 24, height: 6)
        } else {
            CGSize(width: 6, height: 24)
        }
        RoundedRectangle(cornerRadius: 3, style: .continuous)
            .fill(Color.accentColor)
            .overlay(RoundedRectangle(cornerRadius: 3, style: .continuous).strokeBorder(.white.opacity(0.8), lineWidth: 1))
            .frame(width: size.width, height: size.height)
            .shadow(color: .black.opacity(0.25), radius: 1.5, y: 0.5)
            // A larger invisible target makes the handles easy to grab.
            .frame(width: 28, height: 28)
            .contentShape(Rectangle())
            .accessibilityLabel("Crop handle")
    }
}

/// Geometry for moving and resizing a crop rectangle in image pixels.
///
/// Crops are always whole pixels inside the image, so what the size fields show
/// is exactly what gets saved: a fractional origin left by a drag would
/// otherwise grow by a pixel when rounded out at save time.
enum CropMath {
    /// The crop after dragging `handle` (or the whole crop, for nil) by `delta`
    /// image pixels, snapped to whole pixels.
    static func adjusted(
        _ rect: CGRect,
        handle: CropHandle?,
        delta: CGSize,
        ratio: CGFloat?,
        bounds: CGRect,
        minimumSize: CGFloat
    ) -> CGRect {
        guard let handle else {
            // Moving keeps the size; only the origin lands on a whole pixel.
            let moved = moved(rect, by: delta, in: bounds)
            let origin = CGPoint(x: moved.minX.rounded(), y: moved.minY.rounded())
            return snapped(CGRect(origin: origin, size: rect.size), in: bounds)
        }
        let free = freelyResized(rect, handle: handle, delta: delta, bounds: bounds, minimumSize: minimumSize)
        guard let ratio else { return snapped(free, in: bounds) }
        return snapped(constrained(rect, proposal: free, handle: handle, ratio: ratio, bounds: bounds, minimumSize: minimumSize), in: bounds)
    }

    /// `rect` with each edge rounded to the nearest whole pixel and kept inside
    /// `bounds`, at least one pixel across.
    static func snapped(_ rect: CGRect, in bounds: CGRect) -> CGRect {
        let minX: CGFloat = min(max(rect.minX.rounded(), bounds.minX), bounds.maxX - 1)
        let minY: CGFloat = min(max(rect.minY.rounded(), bounds.minY), bounds.maxY - 1)
        let maxX: CGFloat = max(min(rect.maxX.rounded(), bounds.maxX), minX + 1)
        let maxY: CGFloat = max(min(rect.maxY.rounded(), bounds.maxY), minY + 1)
        return CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    /// `rect` resized from its top-left corner to a typed size. The typed side
    /// is kept exactly; with a `ratio`, the other side follows it, rounded to a
    /// whole pixel. Only a size that doesn't fit the image is scaled back.
    static func resized(
        _ rect: CGRect,
        width: Int?,
        height: Int?,
        ratio: CGFloat?,
        bounds: CGRect,
        minimumSize: CGFloat
    ) -> CGRect {
        var newWidth: CGFloat = width.map { CGFloat($0) } ?? rect.width
        var newHeight: CGFloat = height.map { CGFloat($0) } ?? rect.height
        if let ratio {
            if width != nil {
                newHeight = (newWidth / ratio).rounded()
            } else {
                newWidth = (newHeight * ratio).rounded()
            }
        }
        let smallest = CGSize(width: min(minimumSize, bounds.width), height: min(minimumSize, bounds.height))
        let fits = newWidth >= smallest.width && newWidth <= bounds.width
            && newHeight >= smallest.height && newHeight <= bounds.height
        if !fits {
            newWidth = min(max(newWidth, smallest.width), bounds.width)
            newHeight = min(max(newHeight, smallest.height), bounds.height)
            if let ratio {
                // Clamping may have broken the ratio; shrink the longer side back.
                if newWidth / newHeight > ratio {
                    newWidth = (newHeight * ratio).rounded()
                } else {
                    newHeight = (newWidth / ratio).rounded()
                }
            }
        }
        let x: CGFloat = min(rect.minX, bounds.maxX - newWidth)
        let y: CGFloat = min(rect.minY, bounds.maxY - newHeight)
        return snapped(CGRect(x: x, y: y, width: newWidth, height: newHeight), in: bounds)
    }

    static func moved(_ rect: CGRect, by delta: CGSize, in bounds: CGRect) -> CGRect {
        var rect = rect.offsetBy(dx: delta.width, dy: delta.height)
        rect.origin.x = min(max(rect.minX, bounds.minX), bounds.maxX - rect.width)
        rect.origin.y = min(max(rect.minY, bounds.minY), bounds.maxY - rect.height)
        return rect
    }

    static func freelyResized(_ rect: CGRect, handle: CropHandle, delta: CGSize, bounds: CGRect, minimumSize: CGFloat) -> CGRect {
        var minX = rect.minX, maxX = rect.maxX, minY = rect.minY, maxY = rect.maxY
        if handle.movesLeft { minX = min(max(rect.minX + delta.width, bounds.minX), maxX - minimumSize) }
        if handle.movesRight { maxX = max(min(rect.maxX + delta.width, bounds.maxX), minX + minimumSize) }
        if handle.movesTop { minY = min(max(rect.minY + delta.height, bounds.minY), maxY - minimumSize) }
        if handle.movesBottom { maxY = max(min(rect.maxY + delta.height, bounds.maxY), minY + minimumSize) }
        return CGRect(x: minX, y: minY, width: maxX - minX, height: maxY - minY)
    }

    /// Fits `proposal` to `ratio`, anchored at the corner or edge opposite the handle.
    static func constrained(
        _ rect: CGRect,
        proposal: CGRect,
        handle: CropHandle,
        ratio: CGFloat,
        bounds: CGRect,
        minimumSize: CGFloat
    ) -> CGRect {
        let horizontal = handle.movesLeft || handle.movesRight
        let vertical = handle.movesTop || handle.movesBottom
        var width: CGFloat
        if horizontal && vertical {
            // Follow whichever side moved further, relative to its size.
            let widthChange = abs(proposal.width - rect.width) / rect.width
            let heightChange = abs(proposal.height - rect.height) / rect.height
            width = widthChange >= heightChange ? proposal.width : proposal.height * ratio
        } else if horizontal {
            width = proposal.width
        } else {
            width = proposal.height * ratio
        }

        // Room available from the anchor; edge handles grow symmetrically across.
        let maxWidth: CGFloat
        if handle.movesLeft {
            maxWidth = rect.maxX - bounds.minX
        } else if handle.movesRight {
            maxWidth = bounds.maxX - rect.minX
        } else {
            maxWidth = 2 * min(rect.midX - bounds.minX, bounds.maxX - rect.midX)
        }
        let maxHeight: CGFloat
        if handle.movesTop {
            maxHeight = rect.maxY - bounds.minY
        } else if handle.movesBottom {
            maxHeight = bounds.maxY - rect.minY
        } else {
            maxHeight = 2 * min(rect.midY - bounds.minY, bounds.maxY - rect.midY)
        }

        width = min(width, maxWidth, maxHeight * ratio)
        width = max(width, minimumSize, minimumSize * ratio)
        let height = width / ratio

        let x = handle.movesLeft ? rect.maxX - width : handle.movesRight ? rect.minX : rect.midX - width / 2
        let y = handle.movesTop ? rect.maxY - height : handle.movesBottom ? rect.minY : rect.midY - height / 2
        return CGRect(x: x, y: y, width: width, height: height)
    }

    /// The largest rect with `ratio` that fits in `bounds`, centred on `center`
    /// as far as the bounds allow, in whole pixels.
    static func largest(ratio: CGFloat, centeredAt center: CGPoint, in bounds: CGRect) -> CGRect {
        var width: CGFloat = bounds.width
        var height: CGFloat = min((width / ratio).rounded(), bounds.height)
        if (width / ratio).rounded() > bounds.height {
            height = bounds.height
            width = min((height * ratio).rounded(), bounds.width)
        }
        var rect = CGRect(x: (center.x - width / 2).rounded(), y: (center.y - height / 2).rounded(), width: width, height: height)
        rect.origin.x = min(max(rect.minX, bounds.minX), bounds.maxX - width)
        rect.origin.y = min(max(rect.minY, bounds.minY), bounds.maxY - height)
        return snapped(rect, in: bounds)
    }
}
