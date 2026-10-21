/// The wheels that can be summoned during a drag. Convert and Tools wheels are
/// planned (see docs/SPEC.md §6).
enum WheelKind: Hashable {
    case actions
}

/// An entry on the Actions wheel.
enum WheelAction: CaseIterable, Identifiable {
    case newShelf
    case addToShelf
    case airDrop
    case copy
    case move
    case copyPath
    case terminal

    var id: Self { self }

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
