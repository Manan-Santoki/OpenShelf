import Foundation

/// What a save did across the files an editor was given. Files that wouldn't
/// change (an image already smaller than the target, a file with no metadata to
/// remove) are left alone, and the confirmation says so: "1 resized · 2 already
/// smaller".
struct SaveOutcome: Equatable {
    /// Files written, in order.
    var saved: [URL]
    /// Files left alone because saving them would change nothing.
    var unchanged = 0
    /// Past tense for a saved file, e.g. "resized".
    var savedLabel = "saved"
    /// Why a file was left alone, e.g. "already smaller".
    var unchangedLabel = "unchanged"
    /// Shown under a plain "Saved 3 files", e.g. "as PNG".
    var detail: String?

    /// Whether anything was written, so the editor can close.
    var savedAnything: Bool { !saved.isEmpty }

    var title: String {
        if saved.isEmpty { return "Nothing saved" }
        if unchanged > 0 { return "\(saved.count) \(savedLabel) · \(unchanged) \(unchangedLabel)" }
        return saved.count == 1 ? "Saved" : "Saved \(saved.count) files"
    }

    var subtitle: String? {
        if saved.isEmpty {
            guard unchanged > 0 else { return nil }
            let reason = unchangedLabel.prefix(1).uppercased() + unchangedLabel.dropFirst()
            return unchanged == 1 ? reason : "All \(unchanged) \(unchangedLabel)"
        }
        if saved.count == 1 { return saved[0].lastPathComponent }
        return unchanged > 0 ? nil : detail
    }

    var toast: Toast {
        Toast(systemImage: saved.isEmpty ? "info.circle" : "checkmark", title: title, subtitle: subtitle)
    }
}
