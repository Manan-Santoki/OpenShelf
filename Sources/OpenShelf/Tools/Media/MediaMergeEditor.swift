@preconcurrency import AVFoundation
import Observation
import SwiftUI

/// Joins several videos, or several audio files, into one in a chosen order.
struct MediaMergeEditor: View {
    static let windowSize = CGSize(width: 460, height: 480)

    let actions: EditorActions
    @State private var model: MergeModel

    init(urls: [URL], actions: EditorActions) {
        self.actions = actions
        _model = State(initialValue: MergeModel(urls: urls))
    }

    var body: some View {
        EditorChrome(
            title: "Merge",
            primaryTitle: "Merge",
            isPrimaryEnabled: model.items.count >= 2 && model.exportProgress == nil,
            actions: actions,
            onReset: model.reset,
            onPrimary: { model.save(actions: actions) }
        ) {
            VStack(alignment: .leading, spacing: 12) {
                Text("Drag to change the order. They play one after another.")
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)

                List {
                    ForEach(Array(model.items.enumerated()), id: \.element.id) { index, item in
                        HStack(spacing: 10) {
                            Text("\(index + 1)")
                                .font(.system(size: 11, weight: .semibold).monospacedDigit())
                                .foregroundStyle(.secondary)
                                .frame(width: 18)
                            Image(systemName: model.isVideo ? "film" : "waveform")
                                .foregroundStyle(Color.accentColor)
                            Text(item.url.lastPathComponent)
                                .font(.system(size: 12))
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Spacer(minLength: 8)
                            Text(item.duration.map(MediaTime.format) ?? "")
                                .font(.system(size: 11).monospacedDigit())
                                .foregroundStyle(.secondary)
                            Button {
                                model.remove(item.id)
                            } label: {
                                Image(systemName: "minus.circle.fill")
                                    .foregroundStyle(.secondary)
                            }
                            .buttonStyle(.plain)
                            .disabled(model.items.count <= 2)
                            .help("Remove from the merge")
                            .accessibilityLabel("Remove \(item.url.lastPathComponent)")
                        }
                        .padding(.vertical, 2)
                        .accessibilityElement(children: .combine)
                    }
                    .onMove { model.items.move(fromOffsets: $0, toOffset: $1) }
                }
                .listStyle(.bordered(alternatesRowBackgrounds: true))
                .frame(maxHeight: .infinity)

                HStack {
                    Text("Total \(MediaTime.format(model.totalDuration))")
                    Spacer()
                    Text("Saves as “\(model.outputName)”")
                }
                .font(.system(size: 11).monospacedDigit())
                .foregroundStyle(.secondary)

                if let progress = model.exportProgress {
                    MediaExportProgress(progress: progress)
                }
            }
        }
        .task { await model.load() }
        .onDisappear(perform: model.cancel)
    }
}

@MainActor
@Observable
final class MergeModel {
    struct Item: Identifiable {
        let id = UUID()
        let url: URL
        var duration: Double?
    }

    private let original: [URL]
    var items: [Item]
    private(set) var isVideo = true
    private(set) var exportProgress: Double?
    @ObservationIgnored private var exportTask: Task<Void, Never>?

    init(urls: [URL]) {
        original = urls
        items = urls.map { Item(url: $0) }
    }

    var totalDuration: Double { items.compactMap(\.duration).reduce(0, +) }
    var outputName: String { isVideo ? "Merged.mp4" : "Merged.m4a" }

    func load() async {
        var allVideo = true
        for index in items.indices {
            let basics = try? await MediaLoading.basics(of: items[index].url)
            items[index].duration = basics?.duration
            if basics?.hasVideo != true { allVideo = false }
        }
        isVideo = allVideo
    }

    func remove(_ id: Item.ID) {
        guard items.count > 2 else { return }
        items.removeAll { $0.id == id }
    }

    func reset() {
        let durations = Dictionary(items.map { ($0.url, $0.duration) }, uniquingKeysWith: { first, _ in first })
        items = original.map { Item(url: $0, duration: durations[$0] ?? nil) }
    }

    func cancel() {
        exportTask?.cancel()
    }

    func save(actions: EditorActions) {
        exportProgress = 0
        let urls = items.map(\.url)
        exportTask = Task { [weak self] in
            do {
                let output = try await MediaTools.merge(urls) { fraction in
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
