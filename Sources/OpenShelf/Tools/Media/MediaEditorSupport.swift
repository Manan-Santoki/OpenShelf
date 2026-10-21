@preconcurrency import AVFoundation
import AVKit
import SwiftUI

/// "m:ss.s" formatting and parsing for media times.
enum MediaTime {
    static func format(_ seconds: Double) -> String {
        let clamped = max(seconds, 0)
        let minutes = Int(clamped) / 60
        let rest = clamped - Double(minutes * 60)
        return String(format: "%d:%04.1f", minutes, rest)
    }

    /// Accepts "1:02.5", "62.5" or "1:02".
    static func parse(_ text: String) -> Double? {
        let parts = text.trimmingCharacters(in: .whitespaces).split(separator: ":").map(String.init)
        switch parts.count {
        case 1:
            return Double(parts[0])
        case 2:
            guard let minutes = Double(parts[0]), let seconds = Double(parts[1]) else { return nil }
            return minutes * 60 + seconds
        case 3:
            guard let hours = Double(parts[0]), let minutes = Double(parts[1]), let seconds = Double(parts[2]) else { return nil }
            return hours * 3600 + minutes * 60 + seconds
        default:
            return nil
        }
    }

    static func cmTime(_ seconds: Double) -> CMTime {
        CMTime(seconds: seconds, preferredTimescale: 600)
    }
}

/// An editable "m:ss.s" field that commits on Return or when focus leaves it.
struct TimeField: View {
    let label: String
    @Binding var seconds: Double
    @State private var text = ""
    @FocusState private var isFocused: Bool

    var body: some View {
        TextField(label, text: $text)
            .textFieldStyle(.roundedBorder)
            .font(.system(size: 12).monospacedDigit())
            .multilineTextAlignment(.center)
            .frame(width: 76)
            .focused($isFocused)
            .onSubmit(commit)
            .onAppear { text = MediaTime.format(seconds) }
            .onChange(of: seconds) { _, newValue in
                if !isFocused { text = MediaTime.format(newValue) }
            }
            .onChange(of: isFocused) { _, focused in
                if !focused { commit() }
            }
            .accessibilityLabel(label)
    }

    private func commit() {
        if let parsed = MediaTime.parse(text) {
            seconds = parsed
        }
        text = MediaTime.format(seconds)
    }
}

/// Export status shown under an editor while it writes files.
struct MediaExportProgress: View {
    let progress: Double
    var detail: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            ProgressView(value: min(max(progress, 0), 1))
                .progressViewStyle(.linear)
                .tint(.accentColor)
            Text(detail ?? "Exporting… \(Int(progress * 100))%")
                .font(.system(size: 11).monospacedDigit())
                .foregroundStyle(.secondary)
        }
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Export progress")
        .accessibilityValue("\(Int(progress * 100)) percent")
    }
}

/// A rounded card behind previews, matching the image editors.
struct MediaPreviewCard<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(
                RoundedRectangle(cornerRadius: 12, style: .continuous)
                    .fill(Color.black.opacity(0.18))
            )
            .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
    }
}

/// A plain AVPlayer surface without system controls.
struct PlayerSurface: NSViewRepresentable {
    let player: AVPlayer

    func makeNSView(context: Context) -> AVPlayerView {
        let view = AVPlayerView()
        view.controlsStyle = .none
        view.videoGravity = .resizeAspect
        view.player = player
        return view
    }

    func updateNSView(_ view: AVPlayerView, context: Context) {
        if view.player !== player { view.player = player }
    }
}

/// Formats byte counts as "12.4 MB".
enum MediaSize {
    static func format(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    static func fileSize(_ url: URL) -> Int64 {
        Int64((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
    }
}
