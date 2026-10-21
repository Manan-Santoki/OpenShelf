import Observation
import SwiftUI

/// Re-encodes videos smaller, choosing a maximum resolution and a quality level.
struct VideoCompressEditor: View {
    static let windowSize = CGSize(width: 440, height: 420)

    let actions: EditorActions
    @State private var model: VideoCompressModel

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: VideoCompressModel(urls: urls))
    }

    var body: some View {
        EditorChrome(
            title: "Compress Video",
            primaryTitle: model.exportProgress == nil ? "Compress" : "Compressing…",
            isPrimaryEnabled: model.canCompress,
            actions: actions,
            onReset: model.reset,
            onPrimary: { model.save(actions: actions) }
        ) {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 10) {
                    Image(systemName: "film")
                        .font(.system(size: 20, weight: .light))
                        .foregroundStyle(Color.accentColor)
                        .frame(width: 40, height: 40)
                        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.accentColor.opacity(0.12)))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(model.title)
                            .font(.system(size: 13, weight: .semibold))
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Text(model.originalSummary)
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }

                EditorRow(label: "Resolution") {
                    Picker("Resolution", selection: $model.resolution) {
                        ForEach(VideoResolution.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }

                EditorRow(label: "Quality") {
                    Picker("Quality", selection: $model.quality) {
                        ForEach(VideoQuality.allCases) { Text($0.title).tag($0) }
                    }
                    .pickerStyle(.segmented)
                    .labelsHidden()
                }

                sizeSummary

                if let message = model.notice ?? model.sizeAdvice {
                    Label(message, systemImage: model.notice == nil ? "info.circle" : "checkmark.circle")
                        .font(.system(size: 12))
                        .foregroundStyle(.orange)
                        .fixedSize(horizontal: false, vertical: true)
                }

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress, detail: model.progressDetail)
                }
                Spacer(minLength: 0)
            }
        }
        .task { await model.load() }
        .onChange(of: model.resolution) { model.notice = nil }
        .onChange(of: model.quality) { model.notice = nil }
        .onDisappear(perform: model.cancel)
    }

    private var sizeSummary: some View {
        HStack(spacing: 8) {
            Image(systemName: "arrow.down.right.and.arrow.up.left")
                .foregroundStyle(Color.accentColor)
            if let estimate = model.estimate {
                Text(model.urls.count == 1 ? "≈ \(MediaSize.format(estimate))" : "≈ \(MediaSize.format(estimate)) for \(model.urls.count) videos")
                    .font(.system(size: 13, weight: .semibold).monospacedDigit())
                + Text(model.isSmaller ? "  (\(model.savingPercent(estimate))% smaller)" : "  (no smaller)")
                    .font(.system(size: 12))
                    .foregroundStyle(model.isSmaller ? Color.secondary : Color.orange)
            } else {
                Text(model.loadError ?? "Estimating…")
                    .font(.system(size: 12))
                    .foregroundStyle(model.loadError == nil ? Color.secondary : Color.orange)
            }
            Spacer(minLength: 0)
            Text(model.outputSummary)
                .font(.system(size: 11, weight: .semibold).monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .padding(12)
        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.06)))
        .accessibilityElement(children: .combine)
    }
}

@MainActor
@Observable
final class VideoCompressModel {
    let urls: [URL]
    var resolution = VideoResolution.p1080
    var quality = VideoQuality.high
    /// Set after a compress that saved nothing; cleared when the settings change.
    var notice: String?
    private(set) var sources: [VideoSourceInfo]?
    private(set) var loadError: String?
    private(set) var exportProgress: Double?
    private(set) var progressDetail: String?
    @ObservationIgnored private var exportTask: Task<Void, Never>?
    private let originalSize: Int64

    init(urls: [URL]) {
        self.urls = urls
        originalSize = urls.reduce(0) { $0 + MediaSize.fileSize($1) }
    }

    var title: String {
        urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) videos"
    }

    var originalSummary: String {
        guard urls.count == 1, let size = sources?.first?.displaySize else { return "Original \(MediaSize.format(originalSize))" }
        return "\(Int(size.width)) × \(Int(size.height)) · \(MediaSize.format(originalSize))"
    }

    /// What the encoder will be asked for, summed over every video.
    var estimate: Int64? {
        sources.map { VideoCompressionPlan.estimatedBytes(for: $0, resolution: resolution, quality: quality) }
    }

    /// The output format, plus its frame size when there's one video.
    var outputSummary: String {
        guard urls.count == 1, let source = sources?.first else { return "MP4" }
        let size = VideoCompressionPlan(source: source, resolution: resolution, quality: quality).displaySize
        return "MP4 · \(Int(size.width)) × \(Int(size.height))"
    }

    /// How much smaller `estimate` is, at least 1% when it's smaller at all.
    func savingPercent(_ estimate: Int64) -> Int {
        guard originalSize > 0 else { return 0 }
        let kept: Double = Double(estimate) / Double(originalSize) * 100
        return max(1, 100 - Int(kept.rounded(.up)))
    }

    var isSmaller: Bool {
        guard let estimate else { return false }
        return estimate < originalSize
    }

    var canCompress: Bool { exportProgress == nil && isSmaller }

    /// Why Compress is off when these settings wouldn't save anything.
    var sizeAdvice: String? {
        guard let sources, !isSmaller else { return nil }
        return VideoCompressionPlan.advice(for: sources, originalSize: originalSize)
    }

    func load() async {
        var loaded: [VideoSourceInfo] = []
        for url in urls {
            do {
                loaded.append(try await VideoSourceInfo.load(url))
            } catch {
                let text = urls.count == 1 ? "This video can’t be read." : "“\(url.lastPathComponent)” can’t be read."
                loadError = MediaLoading.loadError(error, otherwise: text)
                return
            }
        }
        sources = loaded
    }

    func reset() {
        resolution = .p1080
        quality = .high
    }

    func cancel() {
        exportTask?.cancel()
    }

    func save(actions: EditorActions) {
        notice = nil
        exportProgress = 0
        let urls = urls
        let resolution = resolution
        let quality = quality
        exportTask = Task { [weak self] in
            var outcomes: [URL?] = []
            do {
                for (index, url) in urls.enumerated() {
                    self?.progressDetail = urls.count > 1 ? "Compressing \(index + 1) of \(urls.count)…" : nil
                    let output = try await MediaTools.compress(url, resolution: resolution, quality: quality) { fraction in
                        Task { @MainActor in
                            // Late reports mustn't bring back a finished progress bar.
                            guard self?.exportProgress != nil else { return }
                            self?.exportProgress = (Double(index) + fraction) / Double(urls.count)
                        }
                    }
                    outcomes.append(output)
                }
            } catch is CancellationError {
                self?.finishExport()
                return
            } catch {
                self?.finishExport()
                actions.failed(error)
                return
            }
            self?.finishExport()

            let saved = outcomes.compactMap { $0 }
            let skipped = outcomes.count - saved.count
            if saved.isEmpty {
                let error = MediaToolError.notSmaller(count: skipped)
                self?.notice = error.localizedDescription
                actions.failed(error)
            } else {
                actions.saved(saved)
                if skipped > 0 {
                    ToastController.shared.show(Toast(
                        systemImage: "checkmark",
                        title: "Saved \(saved.count) of \(outcomes.count)",
                        subtitle: "\(skipped) already as small as \(skipped == 1 ? "it gets" : "they get") — not saved"
                    ))
                }
            }
        }
    }

    private func finishExport() {
        exportProgress = nil
        progressDetail = nil
    }
}

extension VideoCompressionPlan {
    /// The estimated total for `sources` at these settings.
    static func estimatedBytes(for sources: [VideoSourceInfo], resolution: VideoResolution, quality: VideoQuality) -> Int64 {
        sources.reduce(0) { $0 + VideoCompressionPlan(source: $1, resolution: resolution, quality: quality).estimatedBytes }
    }

    /// What to tell someone whose settings wouldn't make `sources` smaller:
    /// to go lower if a lower setting would help, or that nothing will.
    static func advice(for sources: [VideoSourceInfo], originalSize: Int64) -> String {
        for resolution in VideoResolution.allCases {
            for quality in VideoQuality.allCases where estimatedBytes(for: sources, resolution: resolution, quality: quality) < originalSize {
                return "Wouldn’t get smaller at these settings. Try a lower resolution or quality."
            }
        }
        return sources.count == 1
            ? "Already as small as it gets."
            : "These videos are already as small as they get."
    }
}
