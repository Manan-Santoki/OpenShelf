@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Cuts a video or audio file down to a time range chosen on a filmstrip or
/// waveform, with live preview.
struct MediaTrimEditor: View {
    static let windowSize = CGSize(width: 600, height: 560)

    let actions: EditorActions
    @State private var model: TrimModel

    init(url: URL, actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: TrimModel(url: url))
    }

    var body: some View {
        EditorChrome(
            title: "Trim",
            primaryTitle: "Save",
            isPrimaryEnabled: model.isReady && model.exportProgress == nil && model.end - model.start > 0.05,
            actions: actions,
            onReset: model.reset,
            onPrimary: { model.save(actions: actions) }
        ) {
            VStack(spacing: 12) {
                MediaPreviewCard {
                    if let error = model.loadError {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.secondary)
                    } else if model.isVideo {
                        PlayerSurface(player: model.player)
                    } else {
                        VStack(spacing: 8) {
                            Image(systemName: "waveform")
                                .font(.system(size: 34, weight: .light))
                            Text(model.url.lastPathComponent)
                                .font(.system(size: 12))
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                        .foregroundStyle(.secondary)
                    }
                }
                .frame(height: model.isVideo ? 250 : 120)

                TrimRangeControl(model: model)
                    .frame(height: model.isVideo ? 58 : 110)

                HStack(spacing: 10) {
                    Button(action: model.togglePlayback) {
                        Image(systemName: model.isPlaying ? "pause.fill" : "play.fill")
                            .frame(width: 18)
                    }
                    .help("Play the selection (Space)")
                    .accessibilityLabel(model.isPlaying ? "Pause" : "Play selection")
                    .keyboardShortcut(.space, modifiers: [])
                    .disabled(!model.isReady)

                    Text("Start").foregroundStyle(.secondary)
                    TimeField(label: "Start time", seconds: model.startBinding)
                    Text("End").foregroundStyle(.secondary)
                    TimeField(label: "End time", seconds: model.endBinding)
                    Spacer(minLength: 0)
                    Text("Length \(MediaTime.format(MediaTime.length(from: model.start, to: model.end)))")
                        .font(.system(size: 11).monospacedDigit())
                        .foregroundStyle(.secondary)
                }
                .font(.system(size: 12))

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress)
                }
            }
        }
        .task { await model.load() }
        .onDisappear(perform: model.stop)
    }
}

@MainActor
@Observable
final class TrimModel {
    let url: URL
    let player: AVPlayer
    private(set) var duration: Double = 0
    private(set) var isVideo = true
    private(set) var isReady = false
    private(set) var loadError: String?
    private(set) var thumbnails: [CGImage] = []
    private(set) var waveform: [Float] = []
    private(set) var currentTime: Double = 0
    private(set) var isPlaying = false
    var start: Double = 0
    var end: Double = 0
    private(set) var exportProgress: Double?

    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var boundaryObserver: Any?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(url: URL) {
        self.url = url
        player = AVPlayer(url: url)
    }

    var startBinding: Binding<Double> {
        Binding { self.start } set: { self.setStart($0) }
    }

    var endBinding: Binding<Double> {
        Binding { self.end } set: { self.setEnd($0) }
    }

    func load() async {
        let asset = AVURLAsset(url: url)
        do {
            duration = try await asset.load(.duration).seconds
            isVideo = try await !asset.loadTracks(withMediaType: .video).isEmpty
        } catch {
            loadError = "This file can’t be read."
            return
        }
        guard duration.isFinite, duration > 0 else {
            loadError = "This file has no playable length."
            return
        }
        end = duration
        isReady = true

        timeObserver = player.addPeriodicTimeObserver(forInterval: MediaTime.cmTime(0.05), queue: .main) { [weak self] time in
            MainActor.assumeIsolated {
                self?.currentTime = time.seconds
                self?.isPlaying = self?.player.rate ?? 0 > 0
            }
        }

        if isVideo {
            thumbnails = await Self.thumbnails(for: url, count: 10, duration: duration)
        } else {
            waveform = await Self.waveform(for: url, bars: 300)
        }
    }

    func stop() {
        exportTask?.cancel()
        player.pause()
        if let timeObserver { player.removeTimeObserver(timeObserver) }
        if let boundaryObserver { player.removeTimeObserver(boundaryObserver) }
        timeObserver = nil
        boundaryObserver = nil
    }

    func reset() {
        start = 0
        end = duration
        seek(to: 0)
    }

    func setStart(_ value: Double) {
        start = Self.clampedStart(value, end: end)
        seek(to: start)
    }

    func setEnd(_ value: Double) {
        end = Self.clampedEnd(value, start: start, duration: duration)
        seek(to: end)
    }

    /// A start on the tenth of a second the fields show, at least a tenth
    /// before `end`, so what's shown is exactly what's exported.
    nonisolated static func clampedStart(_ value: Double, end: Double) -> Double {
        let latest: Double = MediaTime.snapped(end) - MediaTime.precision
        return max(min(MediaTime.snapped(value), latest), 0)
    }

    /// An end on a shown tenth of a second, or the very end of the file when
    /// it shows as the last tenth: the file rarely ends on one, and its last
    /// frames shouldn't be lost.
    nonisolated static func clampedEnd(_ value: Double, start: Double, duration: Double) -> Double {
        let snapped = MediaTime.snapped(value)
        let end = snapped >= MediaTime.snapped(duration) ? duration : snapped
        return max(end, min(start + MediaTime.precision, duration))
    }

    func seek(to seconds: Double) {
        player.pause()
        player.seek(to: MediaTime.cmTime(seconds), toleranceBefore: .zero, toleranceAfter: .zero)
        currentTime = seconds
    }

    /// Plays from the start of the selection and stops at its end.
    func togglePlayback() {
        if player.rate > 0 {
            player.pause()
            return
        }
        if let boundaryObserver { player.removeTimeObserver(boundaryObserver) }
        boundaryObserver = player.addBoundaryTimeObserver(forTimes: [NSValue(time: MediaTime.cmTime(end))], queue: .main) { [weak self] in
            MainActor.assumeIsolated { self?.player.pause() }
        }
        let resumeFrom = currentTime >= start && currentTime < end - 0.05 ? currentTime : start
        player.seek(to: MediaTime.cmTime(resumeFrom), toleranceBefore: .zero, toleranceAfter: .zero) { [weak self] _ in
            MainActor.assumeIsolated { self?.player.play() }
        }
    }

    func save(actions: EditorActions) {
        player.pause()
        exportProgress = 0
        let url = url
        let range = CMTimeRange(start: MediaTime.cmTime(start), end: MediaTime.cmTime(end))
        exportTask = Task { [weak self] in
            do {
                let output = try await MediaTools.trim(url, range: range) { fraction in
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

    // MARK: Loading previews

    nonisolated static func thumbnails(for url: URL, count: Int, duration: Double) async -> [CGImage] {
        let generator = AVAssetImageGenerator(asset: AVURLAsset(url: url))
        generator.appliesPreferredTrackTransform = true
        generator.maximumSize = CGSize(width: 200, height: 200)
        generator.requestedTimeToleranceBefore = MediaTime.cmTime(0.5)
        generator.requestedTimeToleranceAfter = MediaTime.cmTime(0.5)
        var images: [CGImage] = []
        for index in 0..<count {
            let seconds = duration * (Double(index) + 0.5) / Double(count)
            if let frame = try? await generator.image(at: MediaTime.cmTime(seconds)) {
                images.append(frame.image)
            }
        }
        return images
    }

    /// Peak levels for `bars` equal slices of the first audio track, 0...1.
    nonisolated static func waveform(for url: URL, bars: Int) async -> [Float] {
        let asset = AVURLAsset(url: url)
        guard let track = try? await asset.loadTracks(withMediaType: .audio).first,
              let reader = try? AVAssetReader(asset: asset)
        else { return [] }
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsNonInterleaved: false,
        ])
        reader.add(output)
        guard reader.startReading() else { return [] }

        // Peaks of small chunks first, then merged into the requested bar count.
        // Chunks are small enough that even short clips fill every bar.
        let duration = (try? await asset.load(.duration).seconds) ?? 0
        let estimatedSamples = max(duration, 0.1) * 48_000
        let chunkSize = max(16, min(512, Int(estimatedSamples / Double(bars * 2))))
        var chunkPeaks: [Float] = []
        while let buffer = output.copyNextSampleBuffer(), let block = CMSampleBufferGetDataBuffer(buffer) {
            let length = CMBlockBufferGetDataLength(block)
            var data = [Int16](repeating: 0, count: length / 2)
            data.withUnsafeMutableBytes { bytes in
                _ = CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: bytes.baseAddress!)
            }
            var index = 0
            while index < data.count {
                let slice = data[index..<min(index + chunkSize, data.count)]
                let peak = slice.reduce(0) { max($0, abs(Int32($1))) }
                chunkPeaks.append(Float(peak) / Float(Int16.max))
                index += chunkSize
            }
        }
        guard !chunkPeaks.isEmpty else { return [] }
        let perBar = Double(chunkPeaks.count) / Double(bars)
        guard perBar >= 1 else { return chunkPeaks }
        return (0..<bars).map { bar in
            let start = Int(Double(bar) * perBar)
            let end = min(max(Int(Double(bar + 1) * perBar), start + 1), chunkPeaks.count)
            return chunkPeaks[start..<end].max() ?? 0
        }
    }
}

/// Filmstrip or waveform with a draggable start/end selection and a playhead.
private struct TrimRangeControl: View {
    let model: TrimModel
    @State private var dragOrigin: (start: Double, end: Double)?

    private let handleWidth: CGFloat = 12

    var body: some View {
        GeometryReader { proxy in
            let width = proxy.size.width
            let height = proxy.size.height
            let scale = model.duration > 0 ? width / model.duration : 0
            let startX = model.start * scale
            let endX = model.end * scale

            ZStack(alignment: .topLeading) {
                track(width: width, height: height)
                    .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))

                // Dim what will be cut.
                Rectangle().fill(.black.opacity(0.55)).frame(width: max(startX, 0), height: height)
                Rectangle().fill(.black.opacity(0.55)).frame(width: max(width - endX, 0), height: height).offset(x: endX)

                RoundedRectangle(cornerRadius: 6, style: .continuous)
                    .strokeBorder(Color.accentColor, lineWidth: 3)
                    .frame(width: max(endX - startX, handleWidth * 2), height: height)
                    .offset(x: startX)
                    .allowsHitTesting(false)

                if model.isReady {
                    Rectangle()
                        .fill(.white)
                        .frame(width: 2, height: height + 6)
                        .shadow(radius: 1)
                        .offset(x: min(max(model.currentTime * scale - 1, 0), width - 2), y: -3)
                        .allowsHitTesting(false)
                }

                handle(isStart: true)
                    .offset(x: startX)
                    .gesture(dragGesture(isStart: true, scale: scale))
                handle(isStart: false)
                    .offset(x: endX - handleWidth)
                    .gesture(dragGesture(isStart: false, scale: scale))
            }
            .frame(width: width, height: height)
            .contentShape(Rectangle())
            .onTapGesture { location in
                guard scale > 0 else { return }
                model.seek(to: min(max(location.x / scale, model.start), model.end))
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("Trim range")
        .accessibilityValue("From \(MediaTime.format(model.start)) to \(MediaTime.format(model.end))")
    }

    @ViewBuilder
    private func track(width: CGFloat, height: CGFloat) -> some View {
        if model.isVideo {
            HStack(spacing: 0) {
                ForEach(Array(model.thumbnails.enumerated()), id: \.offset) { _, image in
                    Image(decorative: image, scale: 1)
                        .resizable()
                        .scaledToFill()
                        .frame(width: width / CGFloat(max(model.thumbnails.count, 1)), height: height)
                        .clipped()
                }
            }
            .frame(width: width, height: height)
            .background(Color.primary.opacity(0.08))
        } else {
            Canvas { context, size in
                let bars = model.waveform
                guard !bars.isEmpty else { return }
                let barWidth = size.width / CGFloat(bars.count)
                for (index, level) in bars.enumerated() {
                    let barHeight = max(1, CGFloat(level) * size.height * 0.9)
                    let rect = CGRect(x: CGFloat(index) * barWidth, y: (size.height - barHeight) / 2, width: max(barWidth - 0.5, 0.5), height: barHeight)
                    context.fill(Path(roundedRect: rect, cornerRadius: barWidth / 2), with: .color(.accentColor.opacity(0.85)))
                }
            }
            .background(Color.primary.opacity(0.08))
        }
    }

    private func handle(isStart: Bool) -> some View {
        UnevenRoundedRectangle(
            cornerRadii: isStart
                ? RectangleCornerRadii(topLeading: 6, bottomLeading: 6)
                : RectangleCornerRadii(bottomTrailing: 6, topTrailing: 6),
            style: .continuous
        )
        .fill(Color.accentColor)
        .overlay {
            Capsule().fill(.white.opacity(0.9)).frame(width: 2, height: 16)
        }
        .frame(width: handleWidth)
        .contentShape(Rectangle().inset(by: -8))
        .accessibilityLabel(isStart ? "Start handle" : "End handle")
    }

    private func dragGesture(isStart: Bool, scale: CGFloat) -> some Gesture {
        DragGesture(minimumDistance: 0)
            .onChanged { value in
                guard scale > 0 else { return }
                let origin = dragOrigin ?? (model.start, model.end)
                if dragOrigin == nil { dragOrigin = origin }
                let delta = value.translation.width / scale
                if isStart {
                    model.setStart(origin.start + delta)
                } else {
                    model.setEnd(origin.end + delta)
                }
            }
            .onEnded { _ in dragOrigin = nil }
    }
}
