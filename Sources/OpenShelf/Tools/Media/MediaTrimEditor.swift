@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Cuts a video or audio file down to a time range chosen on a filmstrip or
/// waveform, with live preview. Given several files, a filmstrip moves between
/// them; each keeps its own range, or the same seconds can be cut from the
/// start and end of all of them.
struct MediaTrimEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        // The filmstrip mostly fits in the room left under the controls.
        CGSize(width: 600, height: count > 1 ? 600 : 560)
    }

    let actions: EditorActions
    @State private var models: [TrimModel]
    @State private var index = 0
    /// The same seconds cut from the start and end of every file.
    @State private var sameForAll = false
    @State private var thumbnails: [CGImage?]
    @State private var batch = MediaBatchExport()

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _models = State(initialValue: urls.map(TrimModel.init))
        _thumbnails = State(initialValue: urls.map { _ in nil })
    }

    private var model: TrimModel { models[index] }
    private var isBatch: Bool { models.count > 1 }
    private var edited: [Bool] { models.map(\.isEdited) }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        EditorChrome(
            title: "Trim",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: isBatch
                ? batch.progress == nil && editedCount > 0
                : model.isReady && model.exportProgress == nil && model.end - model.start > 0.05,
            actions: actions,
            onReset: model.reset,
            onPrimary: save
        ) {
            VStack(spacing: 12) {
                MediaPreviewCard {
                    if let error = model.loadError {
                        Label(error, systemImage: "exclamationmark.triangle")
                            .foregroundStyle(.secondary)
                    } else if model.isVideo && model.isPlayable {
                        PlayerSurface(player: model.player)
                    } else if model.isVideo {
                        // No playback; the filmstrip frame under the playhead stands in.
                        if let frame = model.stillFrame {
                            Image(decorative: frame, scale: 1)
                                .resizable()
                                .scaledToFit()
                                .accessibilityLabel("Preview frame")
                        }
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
                    .disabled(!model.isReady || !model.isPlayable)

                    Text("Start").foregroundStyle(.secondary)
                    TimeField(label: "Start time", seconds: model.startBinding)
                    Text("End").foregroundStyle(.secondary)
                    TimeField(label: "End time", seconds: model.endBinding)
                    Spacer(minLength: 0)
                    if model.isCutAway {
                        Text("Shorter than these cuts")
                            .font(.system(size: 11))
                            .foregroundStyle(.orange)
                            .help("Nothing would be left of this file, so it isn’t trimmed.")
                    } else {
                        Text("Length \(MediaTime.format(MediaTime.length(from: model.start, to: model.end)))")
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }
                .font(.system(size: 12))
                // Fields start afresh on each file, so typing can't carry over.
                .id(index)

                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: models.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll,
                        itemName: "file"
                    )
                    .onChange(of: index) { old, _ in models[old].player.pause() }
                    .onChange(of: sameForAll) { _, isOn in
                        // Turning it on cuts the shown file's seconds from the rest.
                        if isOn { spread() }
                    }
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
        .onChange(of: Shown(index: index, start: model.start, end: model.end)) { old, new in
            // Only edits to the shown file, not moving to another one.
            if isBatch && sameForAll && old.index == new.index { spread() }
        }
        .onDisappear {
            models.forEach { $0.stop() }
            batch.cancel()
        }
    }

    private var primaryTitle: String {
        guard isBatch else { return "Save" }
        switch editedCount {
        case 0: return "Trim Files"
        case 1: return "Trim 1 File"
        default: return "Trim \(editedCount) Files"
        }
    }

    /// What the range of the shown file depends on, to notice it changing.
    private struct Shown: Equatable {
        var index: Int
        var start: Double
        var end: Double
    }

    /// Loads the one file; or, for several, every file's length (so the same
    /// cuts can be carried to all), then their filmstrip pictures. The shown
    /// file's previews load when it's shown.
    private func load() async {
        guard isBatch else {
            await model.load()
            return
        }
        for model in models { await model.loadBasics() }
        if sameForAll { spread() }
        for index in models.indices where models[index].isVideo {
            thumbnails[index] = await MediaBatch.thumbnail(of: models[index].url)
        }
    }

    /// Cuts the seconds the shown file has cut from its start and end from
    /// every other file's too.
    private func spread() {
        let shown = model
        guard shown.isReady else { return }
        for other in models where other !== shown && other.isReady {
            other.cut(head: shown.start, tail: shown.duration - shown.end)
        }
    }

    private func save() {
        guard isBatch else {
            model.save(actions: actions)
            return
        }
        models.forEach { $0.player.pause() }
        let jobs = models.filter(\.isEdited).map { ($0.url, $0.start, $0.end) }
        let outcome = SaveOutcome(saved: [], unchanged: models.count - jobs.count, savedLabel: "trimmed", unchangedLabel: "not trimmed")
        batch.start(jobs.count, outcome: outcome, actions: actions) { index, progress in
            let (url, start, end) = jobs[index]
            let range = CMTimeRange(start: MediaTime.cmTime(start), end: MediaTime.cmTime(end))
            return [try await MediaTools.trim(url, range: range, progress: progress)]
        }
    }
}

@MainActor
@Observable
final class TrimModel {
    let url: URL
    let player: AVPlayer
    private(set) var duration: Double = 0
    private(set) var isVideo = true
    /// False for files AVFoundation can't read, which are trimmed with ffmpeg.
    private(set) var isPlayable = true
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
    @ObservationIgnored private var basicsTask: Task<Void, Never>?

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

    /// The filmstrip frame nearest the playhead, for videos that can't play.
    var stillFrame: CGImage? {
        guard !thumbnails.isEmpty, duration > 0 else { return nil }
        let index = Int(currentTime / duration * Double(thumbnails.count))
        return thumbnails[min(max(index, 0), thumbnails.count - 1)]
    }

    /// Whether trimming would change the file: something is cut, and
    /// something is left.
    var isEdited: Bool {
        isReady && Self.changes(start: start, end: end, duration: duration)
    }

    /// Whether the same cuts as another file's leave nothing of this one.
    var isCutAway: Bool {
        isReady && end - start < MediaTime.precision / 2
    }

    func load() async {
        await loadBasics()
        await loadPreviews()
    }

    /// Reads the file's length and kind and selects all of it, once however
    /// often it's asked for.
    func loadBasics() async {
        if basicsTask == nil {
            basicsTask = Task {
                do {
                    let basics = try await MediaLoading.basics(of: url)
                    duration = basics.duration
                    isVideo = basics.hasVideo
                    isPlayable = basics.isPlayable
                } catch {
                    loadError = MediaLoading.loadError(error, otherwise: "This file can’t be read.")
                    return
                }
                guard duration.isFinite, duration > 0 else {
                    loadError = "This file has no playable length."
                    return
                }
                end = duration
                isReady = true
            }
        }
        await basicsTask?.value
    }

    /// Follows the playhead and loads the filmstrip or waveform, once.
    func loadPreviews() async {
        guard isReady else { return }
        if timeObserver == nil {
            timeObserver = player.addPeriodicTimeObserver(forInterval: MediaTime.cmTime(0.05), queue: .main) { [weak self] time in
                MainActor.assumeIsolated {
                    self?.currentTime = time.seconds
                    self?.isPlaying = self?.player.rate ?? 0 > 0
                }
            }
        }
        guard thumbnails.isEmpty, waveform.isEmpty else { return }

        // Files only ffmpeg reads get their previews from it, and can't play.
        var thumbnails: [CGImage] = []
        var waveform: [Float] = []
        if isVideo && isPlayable {
            thumbnails = await Self.thumbnails(for: url, count: 10, duration: duration)
        } else if isVideo {
            thumbnails = await MediaLoading.thumbnails(of: url, count: 10, duration: duration)
        } else if isPlayable {
            waveform = await Self.waveform(for: url, bars: 300)
        } else {
            waveform = await MediaLoading.waveform(of: url, bars: 300, duration: duration)
        }
        // Moving to another file cancels this; it loads again when shown.
        guard !Task.isCancelled else { return }
        self.thumbnails = thumbnails
        self.waveform = waveform
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

    /// Cuts `head` seconds from the start and `tail` from the end, as another
    /// file has; selects nothing when that would leave nothing.
    func cut(head: Double, tail: Double) {
        guard isReady else { return }
        if let range = Self.range(cuttingStart: head, end: tail, duration: duration) {
            (start, end) = range
        } else {
            start = min(MediaTime.snapped(max(head, 0)), duration)
            end = start
        }
        seek(to: start)
    }

    /// What's left of a file `duration` long after cutting `head` seconds from
    /// its start and `tail` from its end, on the tenths the fields show (or
    /// the file's very end); nil when nothing would be left.
    nonisolated static func range(cuttingStart head: Double, end tail: Double, duration: Double) -> (start: Double, end: Double)? {
        let start = max(MediaTime.snapped(head), 0)
        let end = duration - max(tail, 0)
        guard end - start >= MediaTime.precision / 2 else { return nil }
        return (start, clampedEnd(end, start: start, duration: duration))
    }

    /// Whether trimming a file `duration` long to `start`–`end` would change
    /// it and leave something of it.
    nonisolated static func changes(start: Double, end: Double, duration: Double) -> Bool {
        end - start >= MediaTime.precision / 2 && (start > 0 || end < duration)
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
