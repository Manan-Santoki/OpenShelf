import Foundation

/// Command-line tools OpenShelf uses when they're on this Mac, but never
/// downloads or installs itself (unlike `ExtraTool`). Without one, its
/// conversions simply aren't offered; Settings → Extra Formats says how to
/// get it, once.
enum OptionalTool: String, CaseIterable, Identifiable, Sendable {
    case whisper
    case calibre

    var id: String { rawValue }

    var name: String {
        switch self {
        case .whisper: "whisper.cpp"
        case .calibre: "calibre"
        }
    }

    /// One short line on what it adds.
    var unlocks: String {
        switch self {
        case .whisper: "Transcripts (TXT) and subtitles (SRT, VTT) of audio and video, made offline"
        case .calibre: "EPUB, MOBI and AZW3 books to each other and to PDF, and PDFs to MOBI and AZW3"
        }
    }

    var homebrewCommand: String {
        switch self {
        case .whisper: "brew install whisper-cpp"
        case .calibre: "brew install --cask calibre"
        }
    }

    enum State: Equatable, Sendable {
        case missing
        /// Installed, but something it needs to work isn't there yet.
        case incomplete(URL)
        case installed(URL)
    }

    func state(in tools: ExternalTools) -> State {
        switch self {
        case .whisper:
            guard let whisper = tools.whisper else { return .missing }
            return tools.whisperModel == nil ? .incomplete(whisper) : .installed(whisper)
        case .calibre:
            return tools.ebookConvert.map(State.installed) ?? .missing
        }
    }

    /// What's still needed for `.incomplete`.
    var completionHint: String {
        switch self {
        case .whisper:
            "Add a speech model to enable it: download a ggml model such as ggml-base.bin from [huggingface.co/ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp/tree/main) into the Models folder."
        case .calibre:
            ""
        }
    }
}
