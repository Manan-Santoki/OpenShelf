@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Crops a video's frame, with the same handles and presets as the image Crop editor.
struct VideoCropEditor: View {
    static let windowSize = CGSize(width: 520, height: 620)

    let actions: EditorActions
    @State private var model: VideoCropModel
    @State private var dragStart: CGRect?

    private let minimumSize: CGFloat = 16

    init(url: URL, actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: VideoCropModel(url: url))
    }

    var body: some View {
        EditorChrome(
            title: "Crop Video",
            primaryTitle: "Apply",
            // Cropping to the whole frame would only re-encode the video.
            isPrimaryEnabled: model.frame != nil && model.exportProgress == nil && model.crop != model.bounds,
            actions: actions,
            onReset: model.reset,
            onPrimary: { model.save(actions: actions) }
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

                EditorRow(label: "Aspect ratio") {
                    Picker("Aspect ratio", selection: $model.ratio) {
                        ForEach(CropRatio.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                    .onChange(of: model.ratio) { _, newRatio in model.snap(to: newRatio) }
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

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress)
                }
            }
        }
        .task { await model.load() }
        .onDisappear(perform: model.cancel)
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
    /// The crop in video pixels, origin at the top left.
    var crop = CGRect(x: 0, y: 0, width: 1, height: 1)
    var ratio = CropRatio.free
    private(set) var exportProgress: Double?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    private let minimumSize: CGFloat = 16

    init(url: URL) {
        self.url = url
    }

    func load() async {
        do {
            guard let size = try await MediaLoading.basics(of: url).displaySize else { throw MediaToolError.noVideo(url) }
            frame = try await MediaLoading.poster(of: url, maximumSize: 1600)
            bounds = CGRect(origin: .zero, size: size)
            crop = bounds
        } catch {
            loadError = MediaLoading.loadError(error, otherwise: "This video can’t be read.")
        }
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
