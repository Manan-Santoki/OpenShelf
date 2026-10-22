import SwiftUI

/// Crops an image to a rectangle chosen with handles, an aspect preset, or exact
/// pixel dimensions. Given several images, a filmstrip moves between them, and
/// one crop can apply to all of them ("the top half") or each can have its own.
struct CropEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 440, height: count > 1 ? 600 + EditorFilmstrip.height : 600)
    }

    let images: [EditableImage]
    let actions: EditorActions

    @State private var index = 0
    /// Each image's crop in its own pixels, origin at the top left.
    @State private var crops: [CGRect]
    /// One crop for every image, kept as fractions of the image.
    @State private var sameForAll = true
    @State private var ratio = CropRatio.free
    @State private var dragStart: CGRect?
    @State private var isWorking = false
    @State private var thumbnails: [CGImage]

    private let minimumSize: CGFloat = 16

    init(images: [EditableImage], actions: EditorActions) {
        self.images = images
        self.actions = actions
        _crops = State(initialValue: images.map { CGRect(origin: .zero, size: $0.pixelSize) })
        _thumbnails = State(initialValue: images.count > 1 ? images.map { $0.thumbnail() } : [])
    }

    private var image: EditableImage { images[index] }
    private var isBatch: Bool { images.count > 1 }

    private func bounds(_ index: Int) -> CGRect { CGRect(origin: .zero, size: images[index].pixelSize) }
    private var bounds: CGRect { bounds(index) }

    /// The image being shown's crop. With Same for All on, changing it carries
    /// the same crop, as fractions, to every other image.
    private var crop: CGRect {
        get { crops[index] }
        nonmutating set {
            guard isBatch, sameForAll else {
                crops[index] = newValue
                return
            }
            let unit = CropMath.unit(newValue, in: bounds)
            crops = images.indices.map { $0 == index ? newValue : CropMath.mapped(unit, to: bounds($0), ratio: ratio.value) }
        }
    }

    /// Images whose crop would change them.
    private var edited: [Bool] { images.indices.map { crops[$0] != bounds($0) } }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: isBatch ? "Crop Images" : "Crop Image",
            primaryTitle: primaryTitle,
            // Cropping to the whole image would only save a copy of it.
            isPrimaryEnabled: !isWorking && editedCount > 0,
            actions: actions,
            onReset: reset,
            onPrimary: apply
        ) {
            VStack(spacing: 14) {
                GeometryReader { proxy in
                    canvas(in: proxy.size)
                }
                .frame(height: 400)

                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: images.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll
                    )
                    .onChange(of: index) { _, _ in dragStart = nil }
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on spreads the shown image's crop to the rest.
                        if isOn { crop = crops[index] }
                    }
                }

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

    private var primaryTitle: String {
        guard isBatch else { return "Apply" }
        switch editedCount {
        case 0: return "Crop Images"
        case 1: return "Crop 1 Image"
        default: return "Crop \(editedCount) Images"
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

    /// Resets the shown image, or every image while they share one crop.
    private func reset() {
        ratio = .free
        crop = bounds
    }

    private func apply() {
        let jobs = images.indices.map { (images[$0], crops[$0]) }
        guard isBatch else {
            do {
                actions.saved([try ImageCropper.save(jobs[0].0, cropTo: jobs[0].1)])
            } catch {
                actions.failed(error)
            }
            return
        }
        isWorking = true
        Task.detached {
            let result = Result { try ImageCropper.run(jobs) }
            await MainActor.run {
                isWorking = false
                switch result {
                case .success(let outcome):
                    actions.finished(outcome)
                case .failure(let error):
                    actions.failed(error)
                }
            }
        }
    }
}

/// Saves cropped copies next to the originals.
enum ImageCropper {
    /// Saves `image` cropped to `crop` (in its pixels) as "<name> Cropped".
    static func save(_ image: EditableImage, cropTo crop: CGRect) throws -> URL {
        // The crop is kept on whole pixels, so this is exactly the size shown.
        let bounds = CGRect(origin: .zero, size: image.pixelSize)
        guard let cropped = image.cgImage.cropping(to: CropMath.snapped(crop, in: bounds)) else {
            throw EditableImageError.unwritable(image.url)
        }
        return try image.save(cropped, suffix: "Cropped")
    }

    /// Crops and saves each image; images whose crop is the whole image are
    /// left alone and counted.
    static func run(_ jobs: [(EditableImage, CGRect)]) throws -> SaveOutcome {
        var outcome = SaveOutcome(saved: [], savedLabel: "cropped", unchangedLabel: "not cropped")
        for (image, crop) in jobs {
            guard crop != CGRect(origin: .zero, size: image.pixelSize) else {
                outcome.unchanged += 1
                continue
            }
            outcome.saved.append(try save(image, cropTo: crop))
        }
        return outcome
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

    /// `rect` as fractions of `bounds`, so one crop can be carried over to
    /// images of other sizes with `mapped(_:to:ratio:)`.
    static func unit(_ rect: CGRect, in bounds: CGRect) -> CGRect {
        CGRect(
            x: (rect.minX - bounds.minX) / bounds.width,
            y: (rect.minY - bounds.minY) / bounds.height,
            width: rect.width / bounds.width,
            height: rect.height / bounds.height
        )
    }

    /// A crop given as fractions of an image, placed on an image of `bounds`
    /// in whole pixels: "the top half" stays the top half whatever the size.
    /// With a `ratio`, the crop keeps that shape on images of another shape,
    /// as wide as the fractions say where it fits, centred where they put it.
    static func mapped(_ unit: CGRect, to bounds: CGRect, ratio: CGFloat?) -> CGRect {
        var rect = CGRect(
            x: bounds.minX + unit.minX * bounds.width,
            y: bounds.minY + unit.minY * bounds.height,
            width: unit.width * bounds.width,
            height: unit.height * bounds.height
        )
        if let ratio {
            var width: CGFloat = rect.width
            var height: CGFloat = width / ratio
            if height > bounds.height {
                height = bounds.height
                width = height * ratio
            }
            if width > bounds.width {
                width = bounds.width
                height = width / ratio
            }
            width = width.rounded()
            height = height.rounded()
            rect = CGRect(x: (rect.midX - width / 2).rounded(), y: (rect.midY - height / 2).rounded(), width: width, height: height)
            rect.origin.x = min(max(rect.minX, bounds.minX), bounds.maxX - width)
            rect.origin.y = min(max(rect.minY, bounds.minY), bounds.maxY - height)
        }
        return snapped(rect, in: bounds)
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
