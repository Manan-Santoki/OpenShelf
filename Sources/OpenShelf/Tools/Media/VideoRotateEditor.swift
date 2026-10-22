@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Turns a video a quarter or half turn, or mirrors it, with a preview. Given
/// several videos, a filmstrip moves between them, and one turn can apply to
/// all of them or each can have its own.
struct VideoRotateEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 460, height: count > 1 ? 520 + EditorFilmstrip.height : 520)
    }

    let actions: EditorActions
    @State private var models: [RotateModel]
    @State private var index = 0
    /// One turn for every video.
    @State private var sameForAll = true
    @State private var thumbnails: [CGImage?]
    @State private var batch = MediaBatchExport()

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _models = State(initialValue: urls.map(RotateModel.init))
        _thumbnails = State(initialValue: urls.map { _ in nil })
    }

    private var model: RotateModel { models[index] }
    private var isBatch: Bool { models.count > 1 }
    /// Every video that can be read gets turned.
    private var edited: [Bool] { models.map { $0.loadError == nil } }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: "Rotate",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: isBatch
                ? batch.progress == nil && editedCount > 0
                : model.frame != nil && model.exportProgress == nil,
            actions: actions,
            onReset: { choose(.right) },
            onPrimary: save
        ) {
            VStack(spacing: 14) {
                GeometryReader { proxy in
                    if let frame = model.frame {
                        preview(frame, in: proxy.size)
                    } else if let error = model.loadError {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.secondary)
                            .frame(maxWidth: .infinity, maxHeight: .infinity)
                    } else {
                        ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
                .frame(height: 300)
                .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(Color.black.opacity(0.18)))

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
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on gives every video the shown one's turn.
                        if isOn { choose(model.rotation) }
                    }
                }

                HStack(spacing: 8) {
                    ForEach(VideoRotation.allCases) { rotation in
                        let isSelected = model.rotation == rotation
                        Button {
                            choose(rotation)
                        } label: {
                            VStack(spacing: 4) {
                                Image(systemName: rotation.systemImage)
                                    .font(.system(size: 16, weight: .semibold))
                                Text(rotation.title)
                                    .font(.system(size: 11, weight: .semibold))
                            }
                            .frame(maxWidth: .infinity)
                            .frame(height: 52)
                            .foregroundStyle(isSelected ? Color.white : Color.primary)
                            .background(
                                RoundedRectangle(cornerRadius: 10, style: .continuous)
                                    .fill(isSelected ? Color.accentColor : Color.primary.opacity(0.07))
                            )
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(rotation == .flipHorizontal ? "Flip horizontally" : "Rotate \(rotation.title)")
                        .accessibilityAddTraits(isSelected ? .isSelected : [])
                    }
                }

                Text(model.rotation.isLossless
                     ? "Turning keeps the original quality; only the orientation changes."
                     : "Flipping re-encodes the video.")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)

                if let progress = isBatch ? batch.progress : model.exportProgress {
                    MediaExportProgress(progress: progress, detail: batch.detail)
                }
                Spacer(minLength: 0)
            }
        }
        .task(id: index) { await model.load() }
        .task {
            guard isBatch else { return }
            for index in models.indices {
                thumbnails[index] = await MediaBatch.thumbnail(of: models[index].url)
            }
        }
        .onDisappear {
            models.forEach { $0.cancel() }
            batch.cancel()
        }
    }

    private var primaryTitle: String {
        guard isBatch else { return "Apply" }
        switch editedCount {
        case 0: return "Rotate Videos"
        case 1: return "Rotate 1 Video"
        default: return "Rotate \(editedCount) Videos"
        }
    }

    /// Turns the shown video, or every video while they share one turn.
    private func choose(_ rotation: VideoRotation) {
        if isBatch && sameForAll {
            models.forEach { $0.rotation = rotation }
        } else {
            model.rotation = rotation
        }
    }

    private func save() {
        guard isBatch else {
            model.save(actions: actions)
            return
        }
        let jobs = models.filter { $0.loadError == nil }.map { ($0.url, $0.rotation) }
        let outcome = SaveOutcome(saved: [], unchanged: models.count - jobs.count, savedLabel: "rotated", unchangedLabel: "not rotated")
        batch.start(jobs.count, outcome: outcome, actions: actions) { index, progress in
            [try await MediaTools.rotate(jobs[index].0, by: jobs[index].1, progress: progress)]
        }
    }

    /// The frame as it will look after the change.
    private func preview(_ frame: CGImage, in available: CGSize) -> some View {
        let size = CGSize(width: frame.width, height: frame.height)
        let quarterTurn = model.rotation == .left || model.rotation == .right
        let rotated = quarterTurn ? CGSize(width: size.height, height: size.width) : size
        let scale = min((available.width - 24) / rotated.width, (available.height - 24) / rotated.height)
        return Image(decorative: frame, scale: 1)
            .resizable()
            .interpolation(.high)
            .frame(width: size.width * scale, height: size.height * scale)
            .scaleEffect(x: model.rotation == .flipHorizontal ? -1 : 1, y: 1)
            .rotationEffect(.degrees(model.rotation.degrees))
            .animation(.spring(response: 0.35, dampingFraction: 0.8), value: model.rotation)
            .frame(width: available.width, height: available.height)
            .accessibilityLabel("Preview of the rotated video")
    }
}

@MainActor
@Observable
final class RotateModel {
    let url: URL
    var rotation = VideoRotation.right
    private(set) var frame: CGImage?
    private(set) var loadError: String?
    private(set) var exportProgress: Double?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(url: URL) {
        self.url = url
    }

    func load() async {
        guard frame == nil, loadError == nil else { return }
        do {
            frame = try await MediaLoading.poster(of: url, maximumSize: 1200)
        } catch {
            // Moving to another video cancels this; it loads again when shown.
            guard !Task.isCancelled else { return }
            loadError = MediaLoading.loadError(error, otherwise: "This video can’t be read.")
        }
    }

    func cancel() {
        exportTask?.cancel()
    }

    func save(actions: EditorActions) {
        exportProgress = 0
        let url = url
        let rotation = rotation
        exportTask = Task { [weak self] in
            do {
                let output = try await MediaTools.rotate(url, by: rotation) { fraction in
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
