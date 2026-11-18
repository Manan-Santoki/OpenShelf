import AppKit

/// The standard About panel (icon, name, version, copyright) with links to
/// the website and the source code. OpenShelf has no app menu, so the
/// menu-bar menu opens it.
@MainActor
enum AboutPanel {
    static let website = URL(string: "https://openshelf.space")!
    static let source = URL(string: "https://github.com/Manan-Santoki/OpenShelf")!

    static func show() {
        NSApp.activate()
        NSApp.orderFrontStandardAboutPanel(options: [.credits: credits])
    }

    /// "openshelf.space · Source on GitHub", centred and clickable.
    static var credits: NSAttributedString {
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        let base: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: NSFont.smallSystemFontSize),
            .foregroundColor: NSColor.secondaryLabelColor,
            .paragraphStyle: paragraph,
        ]
        func link(_ title: String, _ url: URL) -> NSAttributedString {
            NSAttributedString(string: title, attributes: base.merging([.link: url]) { $1 })
        }
        let text = NSMutableAttributedString()
        text.append(link("openshelf.space", website))
        text.append(NSAttributedString(string: "  ·  ", attributes: base))
        text.append(link("Source on GitHub", source))
        return text
    }
}
