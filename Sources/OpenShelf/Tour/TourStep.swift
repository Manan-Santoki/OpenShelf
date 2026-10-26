import AppKit
import Observation

/// Something the person did that a tour step may be waiting for.
enum TourEvent: Equatable {
    /// A wheel opened during a drag of `urls`.
    case wheelOpened(WheelKind, [URL])
    /// A wedge ran on `urls`; `option` when ⌥ was held to choose its settings.
    case ran(WheelCommand, [URL], option: Bool)
    /// A wheel opened for selected files with the keyboard shortcut.
    case keyboardWheelOpened([URL])
}

/// The steps of the guided tour, in order. Each practice step is done when
/// the person does it for real, with a practice file or any file of their own.
enum TourStep: String, CaseIterable, Identifiable {
    case welcome
    case convert
    case markdown
    case tools
    case pdf
    case video
    case shelf
    case keyboard
    case options
    case finish

    var id: String { rawValue }

    /// Steps that wait for the person to do something.
    static var practice: [TourStep] { allCases.filter(\.isPractice) }

    var isPractice: Bool { self != .welcome && self != .finish }

    var title: String {
        switch self {
        case .welcome: "Welcome"
        case .convert: "Convert a photo"
        case .markdown: "Markdown to PDF"
        case .tools: "Edit with Tools"
        case .pdf: "Work with PDFs"
        case .video: "Trim a video"
        case .shelf: "Keep files on a shelf"
        case .keyboard: "No drag needed"
        case .options: "Choose the settings"
        case .finish: "You’re set"
        }
    }

    /// One line on why this matters, under the title.
    var summary: String {
        switch self {
        case .welcome: "Learn OpenShelf by doing it, with files made for practice."
        case .convert: "Any file becomes the format you need, saved right next to it."
        case .markdown: "Notes become a typeset document: headings, lists, tables and code."
        case .tools: "Crop, annotate, redact, resize and more, without opening an editor app."
        case .pdf: "Split, reorder, combine, compress and read text from PDFs."
        case .video: "Trim, crop, rotate, mute or turn clips into GIFs."
        case .shelf: "A shelf holds files while you gather them, then drags them anywhere."
        case .keyboard: "Select files in Finder or on a shelf and open a wheel with a shortcut."
        case .options: "Hold ⌥ as you drop to pick the quality, size or codec for one conversion."
        case .finish: "That’s everything you need. The rest is in Settings."
        }
    }

    var systemImage: String {
        switch self {
        case .welcome: "hand.wave"
        case .convert: "arrow.triangle.2.circlepath"
        case .markdown: "doc.richtext"
        case .tools: "crop"
        case .pdf: "doc.on.doc"
        case .video: "film"
        case .shelf: "tray.and.arrow.down"
        case .keyboard: "keyboard"
        case .options: "slider.horizontal.3"
        case .finish: "checkmark.seal"
        }
    }

    /// The practice file this step is done with.
    var practiceFile: PracticeFile? {
        switch self {
        case .convert, .tools: .photo
        case .markdown: .notes
        case .pdf: .report
        case .video, .options: .clip
        case .shelf: .notes
        case .keyboard: .letter
        case .welcome, .finish: nil
        }
    }

    /// The wheel the step teaches, to show its keys.
    var wheel: WheelKind? {
        switch self {
        case .convert, .markdown, .options: .convert
        case .tools, .pdf, .video: .tools
        case .shelf: .actions
        case .welcome, .keyboard, .finish: nil
        }
    }

    /// The wedge to drop on, as the instructions name it.
    var target: String {
        switch self {
        case .convert: "PNG"
        case .markdown: "PDF"
        case .tools: "Crop"
        case .pdf: "Pages or Split"
        case .video: "Trim"
        case .shelf: "Shelf"
        case .options: "MP4 or GIF"
        case .welcome, .keyboard, .finish: ""
        }
    }

    /// Whether `event` completes this step. Any file of the right kind counts,
    /// so practising with one's own files works too.
    func isCompleted(by event: TourEvent) -> Bool {
        func kinds(_ urls: [URL]) -> Set<FileKind> { Set(urls.map { FileKind(url: $0) }) }
        switch (self, event) {
        case (.convert, .ran(.convert, let urls, _)):
            return kinds(urls).contains(.image)
        case (.markdown, .ran(.convert(.pdf), let urls, _)):
            return urls.contains(where: MarkdownConversion.isMarkdown)
        case (.tools, .ran(.tool, let urls, _)):
            return kinds(urls).contains(.image)
        case (.pdf, .ran(.tool, let urls, _)):
            return kinds(urls).contains(.pdf)
        case (.video, .ran(.tool, let urls, _)), (.video, .ran(.convert(.gif), let urls, _)):
            return !kinds(urls).isDisjoint(with: [.video, .audio])
        case (.shelf, .ran(.action(let action), _, _)):
            return action == .newShelf || action == .addToShelf
        case (.keyboard, .keyboardWheelOpened):
            return true
        case (.options, .ran(.convert, _, let option)):
            return option
        default:
            return false
        }
    }

    /// Whether `event` is this step under way: the right wheel opened for it.
    func isUnderWay(with event: TourEvent) -> Bool {
        guard case .wheelOpened(let kind, _) = event else { return false }
        return kind == wheel
    }
}

/// Follows the tour: which step is showing, which are done, and what the
/// person just did. Fed by the app as wheels open and wedges run.
@MainActor
@Observable
final class TourProgress {
    static let shared = TourProgress()

    private(set) var current: TourStep = .welcome
    private(set) var completed: Set<TourStep> = []
    /// The current step's wheel is open: the next move is the drop.
    private(set) var isUnderWay = false
    /// Changes each time a step is done, to celebrate it.
    private(set) var celebration = 0
    /// Whether the tour window is open, so events are only followed then.
    var isActive = false

    private static let completedKey = "TourCompletedSteps"

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        completed = Set((defaults.stringArray(forKey: Self.completedKey) ?? []).compactMap(TourStep.init(rawValue:)))
    }

    @ObservationIgnored private let defaults: UserDefaults

    var practiceDoneCount: Int { completed.filter(\.isPractice).count }

    func record(_ event: TourEvent) {
        guard isActive else { return }
        if current.isCompleted(by: event) {
            complete(current)
        } else if !completed.contains(current), current.isUnderWay(with: event) {
            isUnderWay = true
        }
    }

    func show(_ step: TourStep) {
        current = step
        isUnderWay = false
    }

    func next() {
        let steps = TourStep.allCases
        guard let index = steps.firstIndex(of: current), index + 1 < steps.count else { return }
        show(steps[index + 1])
    }

    func previous() {
        let steps = TourStep.allCases
        guard let index = steps.firstIndex(of: current), index > 0 else { return }
        show(steps[index - 1])
    }

    func complete(_ step: TourStep) {
        isUnderWay = false
        guard completed.insert(step).inserted else { return }
        celebration += 1
        defaults.set(completed.map(\.rawValue).sorted(), forKey: Self.completedKey)
    }

    /// Starts over, forgetting what was done.
    func reset() {
        completed = []
        defaults.removeObject(forKey: Self.completedKey)
        show(.welcome)
    }
}
