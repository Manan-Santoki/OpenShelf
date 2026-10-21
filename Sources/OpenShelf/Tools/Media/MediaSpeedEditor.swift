@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Speeds up or slows down videos and audio files.
struct MediaSpeedEditor: View {
    static let windowSize = CGSize(width: 420, height: 380)
    static let presets: [Double] = [0.25, 0.5, 0.75, 1.25, 1.5, 2, 4]

    let actions: EditorActions
    @State private var model: SpeedModel

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: SpeedModel(urls: urls))
    }

    var body: some View {
        EditorChrome(
            title: "Speed",
            primaryTitle: "Save",
            isPrimaryEnabled: model.exportProgress == nil && abs(model.rate - 1) > 0.001,
            actions: actions,
            onReset: { model.rate = 2; model.keepsPitch = true },
            onPrimary: { model.save(actions: actions) }
        ) {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 10) {
                    Image(systemName: "gauge.with.dots.needle.67percent")
                        .font(.system(size: 22, weight: .light))
                        .foregroundStyle(Color.accentColor)
                        .frame(width: 40, height: 40)
                        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.accentColor.opacity(0.12)))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(model.title)
                            .font(.system(size: 13, weight: .semibold))
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Text(model.durationSummary)
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }

                HStack(spacing: 6) {
                    ForEach(Self.presets, id: \.self) { preset in
                        let isSelected = abs(model.rate - preset) < 0.001
                        Button {
                            model.rate = preset
                        } label: {
                            Text(MediaTools.speedSuffix(preset).replacingOccurrences(of: "x", with: "×"))
                                .font(.system(size: 12, weight: .semibold).monospacedDigit())
                                .frame(maxWidth: .infinity)
                                .frame(height: 28)
                                .foregroundStyle(isSelected ? Color.white : Color.primary)
                                .background(
                                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                                        .fill(isSelected ? Color.accentColor : Color.primary.opacity(0.07))
                                )
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("\(MediaTools.speedSuffix(preset)) speed")
                        .accessibilityAddTraits(isSelected ? .isSelected : [])
                    }
                }

                EditorRow(label: "Speed") {
                    Slider(value: $model.rate, in: 0.25...4, step: 0.05)
                        .controlSize(.small)
                    Text(MediaTools.speedSuffix(model.rate).replacingOccurrences(of: "x", with: "×"))
                        .font(.system(size: 11).monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(width: 56, alignment: .trailing)
                }

                Toggle("Keep audio pitch", isOn: $model.keepsPitch)
                    .font(.system(size: 12))
                    .help("Off makes voices sound higher when sped up and lower when slowed down")

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress, detail: model.progressDetail)
                }
                Spacer(minLength: 0)
            }
        }
        .task { await model.load() }
        .onDisappear(perform: model.cancel)
    }
}

@MainActor
@Observable
final class SpeedModel {
    let urls: [URL]
    var rate: Double = 2
    var keepsPitch = true
    private(set) var totalDuration: Double = 0
    private(set) var exportProgress: Double?
    private(set) var progressDetail: String?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(urls: [URL]) {
        self.urls = urls
    }

    var title: String {
        urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files"
    }

    var durationSummary: String {
        guard totalDuration > 0 else { return " " }
        return "\(MediaTime.format(totalDuration)) → \(MediaTime.format(totalDuration / rate))"
    }

    func load() async {
        var total: Double = 0
        for url in urls {
            if let duration = try? await AVURLAsset(url: url).load(.duration).seconds, duration.isFinite {
                total += duration
            }
        }
        totalDuration = total
    }

    func cancel() {
        exportTask?.cancel()
    }

    func save(actions: EditorActions) {
        exportProgress = 0
        let urls = urls
        let rate = rate
        let keepsPitch = keepsPitch
        exportTask = Task { [weak self] in
            var outputs: [URL] = []
            do {
                for (index, url) in urls.enumerated() {
                    self?.progressDetail = urls.count > 1 ? "Exporting \(index + 1) of \(urls.count)…" : nil
                    let output = try await MediaTools.changeSpeed(url, rate: rate, keepsPitch: keepsPitch) { fraction in
                        Task { @MainActor in self?.exportProgress = (Double(index) + fraction) / Double(urls.count) }
                    }
                    outputs.append(output)
                }
                actions.saved(outputs)
            } catch is CancellationError {
                self?.exportProgress = nil
            } catch {
                self?.exportProgress = nil
                self?.progressDetail = nil
                actions.failed(error)
            }
        }
    }
}
