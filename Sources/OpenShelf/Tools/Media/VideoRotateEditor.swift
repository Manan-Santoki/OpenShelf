@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Turns a video a quarter or half turn, or mirrors it, with a preview.
struct VideoRotateEditor: View {
    static let windowSize = CGSize(width: 460, height: 520)

    let actions: EditorActions
    @State private var model: RotateModel

    init(url: URL, actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: RotateModel(url: url))
    }

    var body: some View {
        EditorChrome(
            title: "Rotate",
            primaryTitle: "Apply",
            isPrimaryEnabled: model.frame != nil && model.exportProgress == nil,
            actions: actions,
            onReset: { model.rotation = .right },
            onPrimary: { model.save(actions: actions) }
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

                HStack(spacing: 8) {
                    ForEach(VideoRotation.allCases) { rotation in
                        let isSelected = model.rotation == rotation
                        Button {
                            model.rotation = rotation
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

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress)
                }
                Spacer(minLength: 0)
            }
        }
        .task { await model.load() }
        .onDisappear(perform: model.cancel)
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
        do {
            frame = try await MediaLoading.poster(of: url, maximumSize: 1200)
        } catch {
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
