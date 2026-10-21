@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Re-encodes videos smaller, choosing a maximum resolution and a quality level.
struct VideoCompressEditor: View {
    static let windowSize = CGSize(width: 440, height: 400)

    let actions: EditorActions
    @State private var model: VideoCompressModel

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: VideoCompressModel(urls: urls))
    }

    var body: some View {
        EditorChrome(
            title: "Compress Video",
            primaryTitle: "Compress",
            isPrimaryEnabled: model.exportProgress == nil,
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

                HStack(spacing: 8) {
                    Image(systemName: "arrow.down.right.and.arrow.up.left")
                        .foregroundStyle(Color.accentColor)
                    Text(model.estimateText)
                        .font(.system(size: 13, weight: .semibold).monospacedDigit())
                    Spacer(minLength: 0)
                    Text("MP4")
                        .font(.system(size: 11, weight: .semibold))
                        .foregroundStyle(.secondary)
                }
                .padding(12)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.primary.opacity(0.06)))
                .accessibilityElement(children: .combine)

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress, detail: model.progressDetail)
                }
                Spacer(minLength: 0)
            }
        }
        .task(id: model.preset) { await model.updateEstimate() }
        .onDisappear(perform: model.cancel)
    }
}

enum VideoResolution: CaseIterable, Identifiable {
    case original, p1080, p720, p480

    var id: Self { self }

    var title: String {
        switch self {
        case .original: "Original"
        case .p1080: "1080p"
        case .p720: "720p"
        case .p480: "480p"
        }
    }
}

enum VideoQuality: CaseIterable, Identifiable {
    case high, medium, low

    var id: Self { self }

    var title: String {
        switch self {
        case .high: "High"
        case .medium: "Medium"
        case .low: "Low"
        }
    }
}

enum VideoCompressionPreset {
    /// High uses HEVC (smaller at the same quality); Medium and Low use H.264,
    /// with Low dropping one size further.
    static func preset(resolution: VideoResolution, quality: VideoQuality) -> String {
        switch (resolution, quality) {
        case (.original, .high): AVAssetExportPresetHEVCHighestQuality
        case (.original, .medium): AVAssetExportPresetMediumQuality
        case (.original, .low): AVAssetExportPresetLowQuality
        case (.p1080, .high): AVAssetExportPresetHEVC1920x1080
        case (.p1080, .medium): AVAssetExportPreset1920x1080
        case (.p1080, .low): AVAssetExportPreset1280x720
        case (.p720, .high): AVAssetExportPreset1280x720
        case (.p720, .medium): AVAssetExportPreset960x540
        case (.p720, .low): AVAssetExportPreset640x480
        case (.p480, .high), (.p480, .medium): AVAssetExportPreset640x480
        case (.p480, .low): AVAssetExportPresetLowQuality
        }
    }
}

@MainActor
@Observable
final class VideoCompressModel {
    let urls: [URL]
    var resolution = VideoResolution.p1080
    var quality = VideoQuality.high
    private(set) var estimate: Int64?
    private(set) var isEstimating = false
    private(set) var exportProgress: Double?
    private(set) var progressDetail: String?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(urls: [URL]) {
        self.urls = urls
    }

    var preset: String { VideoCompressionPreset.preset(resolution: resolution, quality: quality) }

    var originalSize: Int64 { urls.reduce(0) { $0 + MediaSize.fileSize($1) } }

    var title: String {
        urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) videos"
    }

    var originalSummary: String { "Original \(MediaSize.format(originalSize))" }

    var estimateText: String {
        if isEstimating { return "Estimating…" }
        guard let estimate else { return "Size depends on the video" }
        let suffix = urls.count > 1 ? " for \(urls.count) videos" : ""
        let larger = estimate >= originalSize ? " (no smaller)" : ""
        return "≈ \(MediaSize.format(estimate))\(suffix)\(larger)"
    }

    func reset() {
        resolution = .p1080
        quality = .high
    }

    func cancel() {
        exportTask?.cancel()
    }

    func updateEstimate() async {
        isEstimating = true
        defer { isEstimating = false }
        var total: Int64 = 0
        for url in urls {
            guard let bytes = await MediaTools.estimatedCompressedSize(of: url, preset: preset) else {
                estimate = nil
                return
            }
            if Task.isCancelled { return }
            total += bytes
        }
        estimate = total
    }

    func save(actions: EditorActions) {
        exportProgress = 0
        let urls = urls
        let preset = preset
        exportTask = Task { [weak self] in
            var outputs: [URL] = []
            do {
                for (index, url) in urls.enumerated() {
                    self?.progressDetail = urls.count > 1 ? "Compressing \(index + 1) of \(urls.count)…" : nil
                    let output = try await MediaTools.compress(url, preset: preset) { fraction in
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
