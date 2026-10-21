/// The wheels that can be summoned during a drag.
enum WheelKind: CaseIterable, Hashable {
    case convert
    case tools
    case actions

    var title: String {
        switch self {
        case .convert: "Convert wheel"
        case .tools: "Tools wheel"
        case .actions: "Actions wheel"
        }
    }
}

/// An entry on the Actions wheel.
enum FileAction: CaseIterable {
    case newShelf
    case addToShelf
    case airDrop
    case copy
    case move
    case copyPath
    case terminal

    var title: String {
        switch self {
        case .newShelf: "Shelf"
        case .addToShelf: "Add to Shelf"
        case .airDrop: "AirDrop"
        case .copy: "Copy"
        case .move: "Move"
        case .copyPath: "Copy Path"
        case .terminal: "Terminal"
        }
    }

    var systemImage: String {
        switch self {
        case .newShelf: "tray.and.arrow.down"
        case .addToShelf: "tray.full"
        case .airDrop: "airplayaudio"
        case .copy: "doc.on.doc"
        case .move: "folder"
        case .copyPath: "link"
        case .terminal: "terminal"
        }
    }
}

/// What dropping on a wedge does.
enum WheelCommand: Hashable {
    case action(FileAction)
    case convert(OutputFormat)
    case tool(ImageTool)
}

/// One wedge on a wheel.
struct WheelItem: Identifiable, Hashable {
    let command: WheelCommand
    let title: String
    /// Text-only wedges (output formats) show a larger label instead of an icon.
    let systemImage: String?

    var id: WheelCommand { command }

    static func action(_ action: FileAction) -> WheelItem {
        WheelItem(command: .action(action), title: action.title, systemImage: action.systemImage)
    }

    static func convert(_ format: OutputFormat) -> WheelItem {
        WheelItem(command: .convert(format), title: format.title, systemImage: nil)
    }

    static func tool(_ tool: ImageTool) -> WheelItem {
        WheelItem(command: .tool(tool), title: tool.title, systemImage: tool.systemImage)
    }
}
