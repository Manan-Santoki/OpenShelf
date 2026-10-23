import AppKit
import Observation
import SwiftUI

/// The window ⌥ opens for a Convert wheel format: the output settings that
/// apply to it, prefilled from Settings → Output, for this conversion only
/// unless "Use as default" is ticked.
struct ConvertOptionsView: View {
    let actions: EditorActions
    /// Where the conversion's progress appears, as for a plain drop.
    let location: NSPoint
    @State private var model: ConvertOptionsModel

    init(urls: [URL], format: OutputFormat, options: OutputOptions, location: NSPoint, actions: EditorActions) {
        self.actions = actions
        self.location = location
        _model = State(initialValue: ConvertOptionsModel(urls: urls, format: format, options: options))
    }

    /// Opens the window for converting `urls` to `format`, when the format
    /// has anything to choose for these files. Returns whether it opened.
    @MainActor @discardableResult
    static func present(_ urls: [URL], to format: OutputFormat, near location: NSPoint = NSEvent.mouseLocation) -> Bool {
        let options = OutputOptions(format: format, urls: urls)
        guard !urls.isEmpty, !options.isEmpty else { return false }
        EditorWindowController.present(size: windowSize(for: options), near: location) { actions in
            ConvertOptionsView(urls: urls, format: format, options: options, location: location, actions: actions)
        }
        return true
    }

    /// Room for the rows `options` show (and the custom bitrate's, so
    /// choosing Custom never crowds the window), the estimate and the checkbox.
    static func windowSize(for options: OutputOptions) -> CGSize {
        var rows: Int = 0
        if options.videoCodec { rows += 1 }
        if options.video { rows += 3 }
        if options.audioBitRate { rows += 1 }
        if options.imageQuality { rows += 1 }
        if options.imageMetadata { rows += 1 }
        if options.gif { rows += 2 }
        // Title bar, file name, checkbox and the button bar; then each row,
        // and the estimate line for video.
        let height: CGFloat = 172 + CGFloat(rows) * 30 + (options.video ? 28 : 0)
        return CGSize(width: 380, height: height)
    }

    var body: some View {
        EditorChrome(
            title: "Convert to \(model.format.title)",
            primaryTitle: "Convert",
            actions: actions,
            onReset: { model.reset() },
            onPrimary: { model.convert(actions: actions, near: location) }
        ) {
            VStack(alignment: .leading, spacing: 14) {
                Text(model.subject)
                    .font(.system(size: 12))
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .frame(maxWidth: .infinity)

                Form {
                    let options = model.options
                    if options.video {
                        VideoOutputControls(video: $model.settings.video, showsCodec: options.videoCodec)
                    }
                    if options.audioBitRate {
                        AudioBitRatePicker(kbps: $model.settings.audio.kbps, title: options.video ? "Audio" : "Bitrate")
                    }
                    if options.imageQuality {
                        ImageQualityPicker(quality: $model.settings.image.quality)
                    }
                    if options.imageMetadata {
                        Toggle("Keep metadata", isOn: $model.settings.image.keepsMetadata)
                    }
                    if options.gif {
                        GIFOutputControls(gif: $model.settings.gif)
                    }
                }
                .formStyle(.columns)
                .frame(maxWidth: .infinity)

                if let estimate = model.estimateText {
                    Label(estimate, systemImage: "internaldrive")
                        .font(.system(size: 12).monospacedDigit())
                        .foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity)
                }

                Toggle("Use as default", isOn: $model.usesAsDefault)
                    .toggleStyle(.checkbox)
                    .help("Keep these choices in Settings → Output for later conversions")
                    .frame(maxWidth: .infinity)
            }
        }
        .task { await model.load() }
    }
}

@MainActor
@Observable
final class ConvertOptionsModel {
    let urls: [URL]
    let format: OutputFormat
    let options: OutputOptions
    var settings: OutputSettings
    var usesAsDefault = false
    /// The videos, read once for the size estimate.
    private(set) var sources: [VideoSourceInfo]?
    private(set) var cannotEstimate = false

    init(urls: [URL], format: OutputFormat, options: OutputOptions, settings: OutputSettings = Preferences.shared.output) {
        self.urls = urls
        self.format = format
        self.options = options
        self.settings = settings
    }

    var subject: String {
        urls.count == 1 ? urls[0].lastPathComponent : "\(urls.count) files"
    }

    /// The estimated total output for video, summed over every file; nil
    /// until the videos are read, or when their length is unknown.
    var estimate: Int64? {
        guard let sources else { return nil }
        let total: Int64 = sources.reduce(0) { $0 + VideoCompressionPlan(source: $1, settings: settings).estimatedBytes }
        return total > 0 ? total : nil
    }

    /// "About 24 MB", "Estimating…" while the videos are read, or nil when
    /// there's no estimate to give.
    var estimateText: String? {
        guard options.video, !cannotEstimate else { return nil }
        guard sources != nil else { return "Estimating…" }
        guard let estimate else { return nil }
        let size = MediaSize.format(estimate)
        return urls.count == 1 ? "About \(size)" : "About \(size) for \(urls.count) videos"
    }

    func load() async {
        guard options.video else { return }
        var loaded: [VideoSourceInfo] = []
        for url in urls {
            guard let source = try? await VideoSourceInfo.load(url) else {
                cannotEstimate = true
                return
            }
            loaded.append(source)
        }
        sources = loaded
    }

    /// Back to Automatic for what the window shows.
    func reset() {
        settings = settings.applying(options, from: .automatic)
    }

    /// Converts with these settings, keeping what the window shows as the
    /// default first if asked to.
    func convert(actions: EditorActions, near location: NSPoint) {
        if usesAsDefault {
            let preferences = Preferences.shared
            preferences.output = preferences.output.applying(options, from: settings)
        }
        actions.close()
        ConversionRunner.shared.convert(urls, to: format, settings: settings, near: location)
    }
}
