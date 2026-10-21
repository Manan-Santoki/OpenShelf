@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Cuts a video or audio file into parts: equal parts, every so often, or at
/// chosen times.
struct MediaSplitEditor: View {
    static let windowSize = CGSize(width: 460, height: 520)

    let actions: EditorActions
    @State private var model: SplitModel

    init(url: URL, actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: SplitModel(url: url))
    }

    var body: some View {
        EditorChrome(
            title: "Split",
            primaryTitle: "Split",
            isPrimaryEnabled: model.segments.count >= 2 && model.exportProgress == nil,
            actions: actions,
            onReset: model.reset,
            onPrimary: { model.save(actions: actions) }
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
                                Text(MediaTime.format(segment.duration))
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

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress, detail: model.progressDetail)
                }
            }
        }
        .task { await model.load() }
        .onDisappear(perform: model.cancel)
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
    private(set) var duration: Double = 0
    private(set) var exportProgress: Double?
    private(set) var progressDetail: String?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(url: URL) {
        self.url = url
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
            _ = try plan()
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
        case .atTimes: return try SplitPlan.at(timesText, duration: duration)
        }
    }

    func load() async {
        duration = (try? await AVURLAsset(url: url).load(.duration).seconds) ?? 0
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
