@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Crops a video's frame, with the same handles and presets as the image Crop
/// editor. Given several videos, a filmstrip moves between them, and one crop
/// can apply to all of them (as fractions of each frame) or each can have its own.
struct VideoCropEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 520, height: count > 1 ? 620 + EditorFilmstrip.height : 620)
    }

    let actions: EditorActions
    @State private var models: [VideoCropModel]
    @State private var index = 0
    /// One crop for every video, kept as fractions of its frame.
    @State private var sameForAll = true
    @State private var thumbnails: [CGImage?]
    @State private var batch = MediaBatchExport()
    @State private var dragStart: CGRect?

    private let minimumSize: CGFloat = 16

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _models = State(initialValue: urls.map(VideoCropModel.init))
        _thumbnails = State(initialValue: urls.map { _ in nil })
    }

    private var model: VideoCropModel { models[index] }
    private var isBatch: Bool { models.count > 1 }
    private var edited: [Bool] { models.map(\.isEdited) }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: isBatch ? "Crop Videos" : "Crop Video",
            primaryTitle: primaryTitle,
            // Cropping to the whole frame would only re-encode the video.
            isPrimaryEnabled: isBatch
                ? batch.progress == nil && editedCount > 0
                : model.frame != nil && model.exportProgress == nil && model.crop != model.bounds,
            actions: actions,
            onReset: model.reset,
            onPrimary: save
        ) {
            VStack(spacing: 14) {
                GeometryReader { proxy in
                    if let frame = model.frame {
                        canvas(frame, in: proxy.size)
                    } else if let error = model.loadError {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
                .frame(height: 400)

                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: models.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll,
                        placeholder: "film",
                        itemName: "video"
                    )
                    .onChange(of: index) { _, _ in dragStart = nil }
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on spreads the shown video's crop to the rest.
                        if isOn { spread() }
                    }
                }

                EditorRow(label: "Aspect ratio") {
                    Picker("Aspect ratio", selection: model.ratioBinding) {
                        ForEach(CropRatio.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }

                EditorRow(label: "Size") {
                    HStack(spacing: 6) {
                        Text("W").foregroundStyle(.secondary)
                        TextField("Width", value: model.dimensionBinding(\.width), format: .number.grouping(.never))
                            .frame(width: 72)
                        Text("H").foregroundStyle(.secondary)
                        TextField("Height", value: model.dimensionBinding(\.height), format: .number.grouping(.never))
                            .frame(width: 72)
                        Text("px").foregroundStyle(.secondary)
                        Spacer(minLength: 0)
                    }
                    .textFieldStyle(.roundedBorder)
                    .font(.system(size: 12))
                }

                if let progress = isBatch ? batch.progress : model.exportProgress {
                    MediaExportProgress(progress: progress, detail: batch.detail)
                }
            }
        }
        .task { await load() }
        .task(id: index) {
            if isBatch { await model.load() }
        }
        .onChange(of: Shown(index: index, crop: model.crop, ratio: model.ratio)) { old, new in
            // Only edits to the shown video, not moving to another one.
            if isBatch && sameForAll && old.index == new.index { spread() }
        }
        .onDisappear {
            models.forEach { $0.cancel() }
            batch.cancel()
        }
    }

    private var primaryTitle: String {
        guard isBatch else { return "Apply" }
        switch editedCount {
        case 0: return "Crop Videos"
        case 1: return "Crop 1 Video"
        default: return "Crop \(editedCount) Videos"
        }
    }

    /// What the crop of the shown video depends on, to notice it changing.
    private struct Shown: Equatable {
        var index: Int
        var crop: CGRect
        var ratio: CropRatio
    }

    /// Loads the one video; or, for several, every video's size (so one crop
    /// can be carried to all), then their filmstrip pictures. The shown
    /// video's frame loads when it's shown.
    private func load() async {
        guard isBatch else {
            await model.load()
            return
        }
        for model in models { await model.loadSize() }
        if sameForAll { spread() }
        for index in models.indices {
            thumbnails[index] = await MediaBatch.thumbnail(of: models[index].url)
        }
    }

    /// Gives every other video the shown video's crop, as fractions of its
    /// frame, and its aspect ratio.
    private func spread() {
        let shown = model
        guard shown.isSized else { return }
        let unit = CropMath.unit(shown.crop, in: shown.bounds)
        for other in models where other !== shown && other.isSized {
            other.ratio = shown.ratio
            other.crop = CropMath.mapped(unit, to: other.bounds, ratio: shown.ratio.value)
        }
    }

    private func save() {
        guard isBatch else {
            model.save(actions: actions)
            return
        }
        let jobs = models.filter(\.isEdited).map { ($0.url, $0.crop) }
        let outcome = SaveOutcome(saved: [], unchanged: models.count - jobs.count, savedLabel: "cropped", unchangedLabel: "not cropped")
        batch.start(jobs.count, outcome: outcome, actions: actions) { index, progress in
            [try await MediaTools.crop(jobs[index].0, to: jobs[index].1, progress: progress)]
        }
    }

    private func canvas(_ frame: CGImage, in available: CGSize) -> some View {
        let bounds = model.bounds
        let scale = min(available.width / bounds.width, available.height / bounds.height)
        let fitted = CGSize(width: bounds.width * scale, height: bounds.height * scale)
        let crop = model.crop
        let cropInView = CGRect(x: crop.minX * scale, y: crop.minY * scale, width: crop.width * scale, height: crop.height * scale)

        return ZStack(alignment: .topLeading) {
            Image(decorative: frame, scale: 1)
                .resizable()
                .interpolation(.high)
                .frame(width: fitted.width, height: fitted.height)

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
                VideoCropHandle(handle: handle)
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
                let start = dragStart ?? model.crop
                if dragStart == nil { dragStart = model.crop }
                let delta = CGSize(width: value.translation.width / scale, height: value.translation.height / scale)
                model.crop = CropMath.adjusted(start, handle: handle, delta: delta, ratio: model.ratio.value, bounds: model.bounds, minimumSize: minimumSize)
            }
            .onEnded { _ in dragStart = nil }
    }
}

@MainActor
@Observable
final class VideoCropModel {
    let url: URL
    /// A representative frame, upright.
    private(set) var frame: CGImage?
    private(set) var loadError: String?
    /// The upright video size in pixels; crops are expressed in this space.
    private(set) var bounds = CGRect(x: 0, y: 0, width: 1, height: 1)
    /// Whether `bounds` has been read yet.
    private(set) var isSized = false
    /// The crop in video pixels, origin at the top left.
    var crop = CGRect(x: 0, y: 0, width: 1, height: 1)
    var ratio = CropRatio.free
    private(set) var exportProgress: Double?
    @ObservationIgnored private var exportTask: Task<Void, Never>?
    @ObservationIgnored private var sizeTask: Task<Void, Never>?

    private let minimumSize: CGFloat = 16

    init(url: URL) {
        self.url = url
    }

    /// Whether the crop would change the video.
    var isEdited: Bool { isSized && loadError == nil && crop != bounds }

    func load() async {
        await loadSize()
        guard loadError == nil, frame == nil else { return }
        do {
            frame = try await MediaLoading.poster(of: url, maximumSize: 1600)
        } catch {
            // Moving to another video cancels this; it loads again when shown.
            guard !Task.isCancelled else { return }
            loadError = MediaLoading.loadError(error, otherwise: "This video can’t be read.")
        }
    }

    /// Reads the video's size and starts the crop as the whole frame, once
    /// however often it's asked for.
    func loadSize() async {
        if sizeTask == nil {
            sizeTask = Task {
                do {
                    guard let size = try await MediaLoading.basics(of: url).displaySize else { throw MediaToolError.noVideo(url) }
                    bounds = CGRect(origin: .zero, size: size)
                    crop = bounds
                    isSized = true
                } catch {
                    loadError = MediaLoading.loadError(error, otherwise: "This video can’t be read.")
                }
            }
        }
        await sizeTask?.value
    }

    func cancel() {
        exportTask?.cancel()
    }

    func reset() {
        ratio = .free
        crop = bounds
    }

    func snap(to ratio: CropRatio) {
        guard let value = ratio.value else { return }
        crop = CropMath.largest(ratio: value, centeredAt: CGPoint(x: crop.midX, y: crop.midY), in: bounds)
    }

    /// The aspect ratio; choosing one fits the crop to it. (Only a choice
    /// does: moving between videos with different ratios mustn't refit them.)
    var ratioBinding: Binding<CropRatio> {
        Binding { self.ratio } set: { newRatio in
            self.ratio = newRatio
            self.snap(to: newRatio)
        }
    }

    func dimensionBinding(_ keyPath: KeyPath<CGRect, CGFloat>) -> Binding<Int> {
        Binding {
            Int(self.crop[keyPath: keyPath].rounded())
        } set: { newValue in
            let isWidth = keyPath == \CGRect.width
            self.crop = CropMath.resized(
                self.crop,
                width: isWidth ? newValue : nil,
                height: isWidth ? nil : newValue,
                ratio: self.ratio.value,
                bounds: self.bounds,
                minimumSize: self.minimumSize
            )
        }
    }

    func save(actions: EditorActions) {
        exportProgress = 0
        let url = url
        let rect = crop
        exportTask = Task { [weak self] in
            do {
                let output = try await MediaTools.crop(url, to: rect) { fraction in
                    Task { @MainActor in self?.exportProgress = fraction }
                }
                actions.saved([output])
            } catch is CancellationError {
                self?.exportProgress = nil
            } catch {
                self?.exportProgress = nil
                actions.failed(error)
            }
        }
    }
}

private struct VideoCropHandle: View {
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
            .frame(width: 28, height: 28)
            .contentShape(Rectangle())
            .accessibilityLabel("Crop handle")
    }
}
