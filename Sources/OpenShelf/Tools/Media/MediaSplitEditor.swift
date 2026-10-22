@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Cuts a video or audio file into parts: equal parts, every so often, or at
/// chosen times. Given several files, a filmstrip moves between them; each
/// keeps its own split, or one split applies to all of them where it fits.
struct MediaSplitEditor: View {
    static func windowSize(for count: Int) -> CGSize {
        CGSize(width: 460, height: count > 1 ? 520 + EditorFilmstrip.height : 520)
    }

    let actions: EditorActions
    @State private var models: [SplitModel]
    @State private var index = 0
    /// One split for every file, at the times that fall inside each.
    @State private var sameForAll = false
    @State private var thumbnails: [CGImage?]
    @State private var batch = MediaBatchExport()

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _models = State(initialValue: urls.map(SplitModel.init))
        _thumbnails = State(initialValue: urls.map { _ in nil })
    }

    private var model: SplitModel { models[index] }
    private var isBatch: Bool { models.count > 1 }
    private var edited: [Bool] { models.map { $0.segments.count >= 2 } }
    private var editedCount: Int { edited.filter { $0 }.count }

    var body: some View {
        @Bindable var model = model
        EditorChrome(
            title: "Split",
            primaryTitle: primaryTitle,
            isPrimaryEnabled: isBatch
                ? batch.progress == nil && editedCount > 0
                : model.segments.count >= 2 && model.exportProgress == nil,
            actions: actions,
            onReset: model.reset,
            onPrimary: save
        ) {
            VStack(alignment: .leading, spacing: 12) {
                HStack(spacing: 10) {
                    Image(systemName: "scissors")
                        .font(.system(size: 20, weight: .light))
                        .foregroundStyle(Color.accentColor)
                        .frame(width: 40, height: 40)
                        .background(RoundedRectangle(cornerRadius: 10, style: .continuous).fill(Color.accentColor.opacity(0.12)))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(model.url.lastPathComponent)
                            .font(.system(size: 13, weight: .semibold))
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Text(model.duration > 0 ? MediaTime.format(model.duration) : " ")
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(.secondary)
                    }
                }

                if isBatch {
                    EditorFilmstrip(
                        thumbnails: thumbnails,
                        names: models.map(\.url.lastPathComponent),
                        selection: $index,
                        edited: edited,
                        sameForAll: $sameForAll,
                        itemName: "file"
                    )
                    .onChange(of: sameForAll) { _, isOn in
                        models.forEach { $0.keepsTimesInside = isOn }
                        // Turning it on gives every file the shown one's split.
                        if isOn { spread() }
                    }
                }

                Picker("Split", selection: $model.mode) {
                    ForEach(SplitMode.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented)
                .labelsHidden()

                Group {
                    switch model.mode {
                    case .equalParts:
                        EditorRow(label: "Parts") {
                            Stepper(value: $model.parts, in: 2...20) {
                                Text("\(model.parts)").monospacedDigit()
                            }
                            Spacer(minLength: 0)
                        }
                    case .everyInterval:
                        EditorRow(label: "Every") {
                            TextField("Length", value: $model.interval, format: .number)
                                .textFieldStyle(.roundedBorder)
                                .frame(width: 64)
                            Picker("Unit", selection: $model.intervalInMinutes) {
                                Text("seconds").tag(false)
                                Text("minutes").tag(true)
                            }
                            .labelsHidden()
                            .frame(width: 100)
                            Spacer(minLength: 0)
                        }
                    case .atTimes:
                        EditorRow(label: "Split at") {
                            TextField("0:30, 1:15, 2:00", text: $model.timesText)
                                .textFieldStyle(.roundedBorder)
                        }
                    }
                }
                .font(.system(size: 12))

                if let problem = model.problem {
                    Label(problem, systemImage: "exclamationmark.triangle")
                        .font(.system(size: 11))
                        .foregroundStyle(.orange)
                }

                ScrollView {
                    VStack(spacing: 0) {
                        ForEach(Array(model.segments.enumerated()), id: \.offset) { index, segment in
                            HStack {
                                Text("Part \(index + 1)")
                                    .font(.system(size: 12, weight: .medium))
                                Spacer()
                                Text("\(MediaTime.format(segment.start)) – \(MediaTime.format(segment.end))")
                                    .foregroundStyle(.secondary)
                                Text(MediaTime.format(MediaTime.length(from: segment.start, to: segment.end)))
                                    .frame(width: 56, alignment: .trailing)
                            }
                            .font(.system(size: 11).monospacedDigit())
                            .padding(.horizontal, 10)
                            .padding(.vertical, 6)
                            .background(index.isMultiple(of: 2) ? Color.primary.opacity(0.04) : .clear)
                            .accessibilityElement(children: .combine)
                        }
                    }
                }
                .frame(maxHeight: .infinity)
                .background(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(Color.primary.opacity(0.1)))

                Text(model.segments.count >= 2 ? "Creates \(model.segments.count) files: “\(model.partName)”…" : " ")
                    .font(.system(size: 11))
                    .foregroundStyle(.secondary)

                if let progress = isBatch ? batch.progress : model.exportProgress {
                    MediaExportProgress(progress: progress, detail: isBatch ? batch.detail : model.progressDetail)
                }
            }
        }
        .task { await load() }
        .onChange(of: Shown(index: index, settings: model.settings)) { old, new in
            // Only edits to the shown file, not moving to another one.
            if isBatch && sameForAll && old.index == new.index { spread() }
        }
        .onDisappear {
            models.forEach { $0.cancel() }
            batch.cancel()
        }
    }

    private var primaryTitle: String {
        guard isBatch else { return "Split" }
        switch editedCount {
        case 0: return "Split Files"
        case 1: return "Split 1 File"
        default: return "Split \(editedCount) Files"
        }
    }

    /// What the split of the shown file depends on, to notice it changing.
    private struct Shown: Equatable {
        var index: Int
        var settings: SplitSettings
    }

    /// Loads every file's length, then, for several, their filmstrip pictures.
    private func load() async {
        for model in models { await model.load() }
        guard isBatch else { return }
        for index in models.indices {
            thumbnails[index] = await MediaBatch.thumbnail(of: models[index].url)
        }
    }

    /// Gives every other file the shown file's split.
    private func spread() {
        let shown = model
        for other in models where other !== shown {
            other.settings = shown.settings
        }
    }

    private func save() {
        guard isBatch else {
            model.save(actions: actions)
            return
        }
        let jobs = models.map { ($0.url, $0.segments) }.filter { $0.1.count >= 2 }
        let outcome = SaveOutcome(saved: [], unchanged: models.count - jobs.count, savedLabel: "saved", unchangedLabel: "not split")
        batch.start(jobs.count, outcome: outcome, actions: actions) { index, progress in
            try await MediaTools.split(jobs[index].0, into: jobs[index].1) { fraction, _ in progress(fraction) }
        }
    }
}

/// The choices in the Split editor, which one file can pass to others.
struct SplitSettings: Equatable {
    var mode: SplitMode
    var parts: Int
    var interval: Double
    var intervalInMinutes: Bool
    var timesText: String
}

extension SplitPlan {
    /// Cuts at the times in `text` that fall inside a file `duration` long,
    /// leaving out later ones, for one list of times shared by files of
    /// different lengths. No parts when none fall inside.
    static func at(_ text: String, inside duration: Double) throws -> [SplitSegment] {
        // Read every time as if the file were endless, so only unreadable
        // times are a problem, then keep the cuts this file reaches.
        let cuts = try at(text, duration: .infinity).dropFirst().map(\.start).filter { $0 < duration }
        guard duration > 0, !cuts.isEmpty else { return [] }
        let edges = [0] + cuts + [duration]
        return zip(edges, edges.dropFirst()).map { SplitSegment(start: $0, end: $1) }
    }
}

@MainActor
@Observable
final class SplitModel {
    let url: URL
    var mode = SplitMode.equalParts
    var parts = 2
    var interval = 30.0
    var intervalInMinutes = false
    var timesText = ""
    /// Whether times past the end are left out rather than a problem, while
    /// one split is shared by files of different lengths.
    var keepsTimesInside = false
    private(set) var duration: Double = 0
    private(set) var exportProgress: Double?
    private(set) var progressDetail: String?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(url: URL) {
        self.url = url
    }

    var settings: SplitSettings {
        get { SplitSettings(mode: mode, parts: parts, interval: interval, intervalInMinutes: intervalInMinutes, timesText: timesText) }
        set {
            mode = newValue.mode
            parts = newValue.parts
            interval = newValue.interval
            intervalInMinutes = newValue.intervalInMinutes
            timesText = newValue.timesText
        }
    }

    /// The parts for the current settings, or none while they're invalid.
    var segments: [SplitSegment] {
        (try? plan()) ?? []
    }

    /// Why the current settings can't be used, if they can't.
    var problem: String? {
        guard duration > 0 else { return nil }
        if mode == .atTimes, timesText.trimmingCharacters(in: .whitespaces).isEmpty { return nil }
        do {
            let segments = try plan()
            if keepsTimesInside && segments.count < 2 {
                return "No split falls inside this file, so it’s left whole."
            }
            return nil
        } catch {
            return error.localizedDescription
        }
    }

    var partName: String {
        OutputNaming.destination(for: url, suffix: "Part 1", pathExtension: url.pathExtension).lastPathComponent
    }

    private func plan() throws -> [SplitSegment] {
        guard duration > 0 else { return [] }
        switch mode {
        case .equalParts: return SplitPlan.equalParts(duration: duration, count: parts)
        case .everyInterval: return try SplitPlan.every(interval * (intervalInMinutes ? 60 : 1), duration: duration)
        case .atTimes: return keepsTimesInside ? try SplitPlan.at(timesText, inside: duration) : try SplitPlan.at(timesText, duration: duration)
        }
    }

    func load() async {
        duration = (try? await MediaLoading.basics(of: url).duration) ?? 0
    }

    func reset() {
        mode = .equalParts
        parts = 2
        interval = 30
        intervalInMinutes = false
        timesText = ""
    }

    func cancel() {
        exportTask?.cancel()
    }

    func save(actions: EditorActions) {
        let segments = segments
        guard segments.count >= 2 else { return }
        exportProgress = 0
        let url = url
        exportTask = Task { [weak self] in
            do {
                let outputs = try await MediaTools.split(url, into: segments) { fraction, part in
                    Task { @MainActor in
                        self?.exportProgress = fraction
                        self?.progressDetail = "Exporting part \(part) of \(segments.count)…"
                    }
                }
                actions.saved(outputs)
            } catch is CancellationError {
                self?.exportProgress = nil
            } catch {
                self?.exportProgress = nil
                actions.failed(error)
            }
        }
    }
}
