import Foundation

/// A tool that runs without a window, offered as a Shortcuts action. Each one
/// is the engine behind a Tools-wheel entry, run with that tool's defaults.
enum IntentTool: CaseIterable, Hashable, Sendable {
    case compress
    case extractText
    case removeBackground
    case combinePDF
    case removeAudio
    case readQR

    var title: String {
        switch self {
        case .compress: "Compress"
        case .extractText: "Extract Text"
        case .removeBackground: "Remove Background"
        case .combinePDF: "Combine into PDF"
        case .removeAudio: "Remove Audio"
        case .readQR: "Read QR Code"
        }
    }

    /// The kinds of file the tool works on.
    var acceptedKinds: Set<FileKind> {
        switch self {
        case .compress, .extractText, .combinePDF: [.image, .pdf]
        case .removeBackground, .readQR: [.image]
        case .removeAudio: [.video]
        }
    }

    /// The Tools-wheel entries this action does the work of, for a file of `kind`.
    func wheelTools(for kind: FileKind) -> [FileTool] {
        switch (self, kind) {
        case (.compress, .pdf): [.compressPDF]
        case (.compress, _): [.compress]
        case (.extractText, _): [.ocr]
        case (.removeBackground, _): [.removeBackground]
        case (.combinePDF, _): [.combinePDF]
        case (.removeAudio, _): [.mute]
        case (.readQR, _): [.readQR]
        }
    }
}

/// The decisions behind the Shortcuts actions, kept apart from App Intents
/// so tests can check them directly.
enum IntentRouting {
    /// What Convert Files does with one file.
    enum ConvertStep: Equatable {
        /// Already in the target format: handed on unchanged, so an
        /// automation over a mixed folder doesn't fail on it.
        case keep
        case convert
        /// Not a conversion OpenShelf offers for this file.
        case unsupported
    }

    /// - Parameter available: The Convert wheel's formats for the file.
    static func convertStep(pathExtension: String, to format: OutputFormat, available: [OutputFormat]) -> ConvertStep {
        if format.fileExtension != nil, format.matches(extension: pathExtension) { return .keep }
        return available.contains(format) ? .convert : .unsupported
    }

    /// Files handed over without a place on disk, or from a folder OpenShelf
    /// can't write to, are copied somewhere it can first, since results are
    /// saved next to the file they came from.
    static func needsStaging(hasFileURL: Bool, folderIsWritable: Bool) -> Bool {
        !hasFileURL || !folderIsWritable
    }

    static func unsupportedMessage(name: String, format: OutputFormat, available: [OutputFormat]) -> String {
        // The wheel always offers ZIP; it isn't worth listing here.
        let others = available.filter { $0 != .zip && $0 != format }
        guard !others.isEmpty else { return "“\(name)” can’t be converted to \(format.title)." }
        return "“\(name)” can’t be converted to \(format.title). It can become \(others.map(\.title).joined(separator: ", "))."
    }

    static func unacceptedMessage(name: String, tool: IntentTool) -> String {
        let kinds = tool.acceptedKinds
        let what = if kinds == [.image] {
            "images"
        } else if kinds == [.video] {
            "videos"
        } else {
            "images and PDFs"
        }
        return "\(tool.title) works on \(what); “\(name)” isn’t one."
    }

    /// One message for every file that failed in a batch, or nil when none did:
    /// "“a.png”: reason", or the first one and how many more.
    static func failureMessage(_ failures: [(name: String, reason: String)]) -> String? {
        guard let first = failures.first else { return nil }
        let message = "“\(first.name)”: \(first.reason)"
        switch failures.count {
        case 1: return message
        case 2: return "\(message) (and 1 more file failed)"
        default: return "\(message) (and \(failures.count - 1) more files failed)"
        }
    }
}
