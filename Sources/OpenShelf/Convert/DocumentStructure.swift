import AppKit

/// A run of text with simple inline styling.
struct TextRun: Equatable {
    var text: String
    var bold = false
    var italic = false
    var link: String?
}

/// One block of a document, as Markdown and EPUB see it.
enum TextBlock: Equatable {
    case heading(level: Int, runs: [TextRun])
    case paragraph([TextRun])
    case listItem(ordered: Bool, runs: [TextRun])
}

/// Turns documents into headings, paragraphs and lists, and writes those as
/// Markdown or XHTML.
enum DocumentStructure {
    // MARK: Reading

    /// Blocks from rich text. Headings are recognised by a font noticeably
    /// larger than the body text; lists by paragraph list styles or bullet
    /// characters.
    static func blocks(from text: NSAttributedString) -> [TextBlock] {
        let string = text.string as NSString
        let bodySize = mostCommonFontSize(in: text)
        var blocks: [TextBlock] = []

        string.enumerateSubstrings(in: NSRange(location: 0, length: string.length), options: [.byParagraphs, .substringNotRequired]) { _, range, _, _ in
            guard range.length > 0 else { return }
            var runs = runs(in: text, range: range)
            let plain = runs.map(\.text).joined()
            guard !plain.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }

            let style = text.attribute(.paragraphStyle, at: range.location, effectiveRange: nil) as? NSParagraphStyle
            if let list = style?.textLists.last {
                stripListMarker(from: &runs)
                blocks.append(.listItem(ordered: isOrdered(list), runs: runs))
                return
            }
            if let ordered = listMarkerKind(plain) {
                stripListMarker(from: &runs)
                blocks.append(.listItem(ordered: ordered, runs: runs))
                return
            }
            let size = (text.attribute(.font, at: range.location, effectiveRange: nil) as? NSFont)?.pointSize ?? bodySize
            if let level = headingLevel(size: size, bodySize: bodySize, allBold: runs.allSatisfy(\.bold), length: plain.count) {
                blocks.append(.heading(level: level, runs: runs.map { TextRun(text: $0.text, link: $0.link) }))
            } else {
                blocks.append(.paragraph(runs))
            }
        }
        return blocks
    }

    /// Blocks from plain text: paragraphs separated by blank lines, keeping
    /// line breaks inside them.
    static func blocks(fromPlainText string: String) -> [TextBlock] {
        paragraphs(in: string).map { .paragraph([TextRun(text: $0)]) }
    }

    /// Blocks from Markdown: ATX headings, bullet and numbered lists, and
    /// paragraphs, with bold, italic and links inside them.
    static func blocks(fromMarkdown string: String) -> [TextBlock] {
        var blocks: [TextBlock] = []
        var paragraph: [String] = []

        func flush() {
            guard !paragraph.isEmpty else { return }
            blocks.append(.paragraph(inlineRuns(paragraph.joined(separator: " "))))
            paragraph.removeAll()
        }

        for line in string.replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n") {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if trimmed.isEmpty {
                flush()
            } else if let match = trimmed.firstMatch(of: /^(#{1,6})\s+(.*?)\s*#*$/) {
                flush()
                blocks.append(.heading(level: match.1.count, runs: inlineRuns(String(match.2))))
            } else if let match = trimmed.firstMatch(of: /^[-*+]\s+(.*)$/) {
                flush()
                blocks.append(.listItem(ordered: false, runs: inlineRuns(String(match.1))))
            } else if let match = trimmed.firstMatch(of: /^\d+[.)]\s+(.*)$/) {
                flush()
                blocks.append(.listItem(ordered: true, runs: inlineRuns(String(match.1))))
            } else {
                paragraph.append(trimmed)
            }
        }
        flush()
        return blocks
    }

    /// Splits text into paragraphs at blank lines.
    static func paragraphs(in string: String) -> [String] {
        string.replacingOccurrences(of: "\r\n", with: "\n")
            .split(separator: /\n[ \t]*\n/)
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }
    }

    // MARK: Writing

    static func markdown(_ blocks: [TextBlock]) -> String {
        var output = ""
        var previous: TextBlock?
        var number = 0
        for block in blocks {
            var continuesList = false
            if case .listItem(let ordered, _) = block, case .listItem(let wasOrdered, _) = previous, ordered == wasOrdered {
                continuesList = true
            }
            // Blocks are separated by a blank line; items of one list sit on consecutive lines.
            if previous != nil { output += continuesList ? "\n" : "\n\n" }
            switch block {
            case .heading(let level, let runs):
                output += String(repeating: "#", count: level) + " " + markdown(runs)
            case .paragraph(let runs):
                output += markdown(runs)
            case .listItem(let ordered, let runs):
                number = continuesList ? number + 1 : 1
                output += (ordered ? "\(number). " : "- ") + markdown(runs)
            }
            previous = block
        }
        return output + "\n"
    }

    /// The `<body>` contents of an XHTML document for `blocks`.
    static func xhtml(_ blocks: [TextBlock]) -> String {
        var html: [String] = []
        var openList: Bool?
        func closeList() {
            if let ordered = openList { html.append(ordered ? "</ol>" : "</ul>") }
            openList = nil
        }
        for block in blocks {
            switch block {
            case .heading(let level, let runs):
                closeList()
                html.append("<h\(level)>\(xhtml(runs))</h\(level)>")
            case .paragraph(let runs):
                closeList()
                html.append("<p>\(xhtml(runs).replacingOccurrences(of: "\n", with: "<br/>"))</p>")
            case .listItem(let ordered, let runs):
                if openList != ordered {
                    closeList()
                    html.append(ordered ? "<ol>" : "<ul>")
                    openList = ordered
                }
                html.append("<li>\(xhtml(runs))</li>")
            }
        }
        closeList()
        return html.joined(separator: "\n")
    }

    // MARK: Inline text

    static func markdown(_ runs: [TextRun]) -> String {
        runs.map { run in
            // Emphasis markers must hug the text, so keep spaces outside them.
            let core = run.text.trimmingCharacters(in: .whitespaces)
            guard !core.isEmpty else { return run.text }
            let leading = String(run.text.prefix { $0 == " " || $0 == "\t" })
            let trailing = String(run.text.reversed().prefix { $0 == " " || $0 == "\t" })
            var text = escapeMarkdown(core)
            if run.bold && run.italic { text = "***\(text)***" } else if run.bold { text = "**\(text)**" } else if run.italic { text = "*\(text)*" }
            if let link = run.link { text = "[\(text)](\(link))" }
            return leading + text + trailing
        }
        .joined()
    }

    static func xhtml(_ runs: [TextRun]) -> String {
        runs.map { run in
            var text = run.text.xmlEscaped
            if run.italic { text = "<em>\(text)</em>" }
            if run.bold { text = "<strong>\(text)</strong>" }
            if let link = run.link { text = "<a href=\"\(link.xmlEscaped)\">\(text)</a>" }
            return text
        }
        .joined()
    }

    private static func escapeMarkdown(_ text: String) -> String {
        var result = ""
        for character in text {
            if "\\`*_[]".contains(character) { result.append("\\") }
            result.append(character)
        }
        return result
    }

    /// Inline Markdown (bold, italic, links) as runs.
    private static func inlineRuns(_ text: String) -> [TextRun] {
        let options = AttributedString.MarkdownParsingOptions(interpretedSyntax: .inlineOnlyPreservingWhitespace)
        guard let parsed = try? AttributedString(markdown: text, options: options) else {
            return [TextRun(text: text)]
        }
        return parsed.runs.map { run in
            let intent = run.inlinePresentationIntent ?? []
            return TextRun(
                text: String(parsed[run.range].characters),
                bold: intent.contains(.stronglyEmphasized),
                italic: intent.contains(.emphasized),
                link: run.link?.absoluteString
            )
        }
    }

    // MARK: Rich-text helpers

    private static func runs(in text: NSAttributedString, range: NSRange) -> [TextRun] {
        var runs: [TextRun] = []
        text.enumerateAttributes(in: range) { attributes, subrange, _ in
            let piece = (text.string as NSString).substring(with: subrange)
            let traits = (attributes[.font] as? NSFont)?.fontDescriptor.symbolicTraits ?? []
            let link: String? = switch attributes[.link] {
            case let url as URL: url.absoluteString
            case let string as String: string
            default: nil
            }
            let run = TextRun(text: piece, bold: traits.contains(.bold), italic: traits.contains(.italic), link: link)
            // Merge neighbours with the same styling.
            if let last = runs.last, last.bold == run.bold, last.italic == run.italic, last.link == run.link {
                runs[runs.count - 1].text += piece
            } else {
                runs.append(run)
            }
        }
        return runs
    }

    private static func mostCommonFontSize(in text: NSAttributedString) -> CGFloat {
        var weights: [CGFloat: Int] = [:]
        text.enumerateAttribute(.font, in: NSRange(location: 0, length: text.length)) { value, range, _ in
            let size = (value as? NSFont)?.pointSize ?? 12
            weights[size, default: 0] += range.length
        }
        return weights.max { $0.value < $1.value }?.key ?? 12
    }

    static func headingLevel(size: CGFloat, bodySize: CGFloat, allBold: Bool, length: Int) -> Int? {
        guard length <= 200 else { return nil }
        let ratio = size / max(bodySize, 1)
        if ratio >= 1.6 { return 1 }
        if ratio >= 1.3 { return 2 }
        if ratio >= 1.1 { return 3 }
        // A short, all-bold line set a touch larger reads as a minor heading.
        if allBold && ratio >= 1.05 { return 3 }
        return nil
    }

    private static func isOrdered(_ list: NSTextList) -> Bool {
        let format = list.markerFormat.rawValue
        return ["decimal", "roman", "alpha"].contains { format.localizedCaseInsensitiveContains($0) }
    }

    /// Whether a paragraph starts with a typed list marker ("•", "-", "1."):
    /// true for numbered, false for bullets, nil when it isn't a list item.
    static func listMarkerKind(_ text: String) -> Bool? {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        if trimmed.firstMatch(of: /^[•◦▪‣·]\s+/) != nil { return false }
        if trimmed.firstMatch(of: /^\d+[.)]\s+/) != nil { return true }
        // Cocoa writes list markers between tabs, e.g. "\t1\tItem" or "\t•\tItem".
        if text.firstMatch(of: /^\t\d+[.)]?\t/) != nil { return true }
        if text.firstMatch(of: /^\t[-–•◦▪‣·]\t/) != nil { return false }
        return nil
    }

    /// Removes a leading bullet or number (including the tab Cocoa puts
    /// around list markers) from the first run.
    private static func stripListMarker(from runs: inout [TextRun]) {
        guard !runs.isEmpty else { return }
        runs[0].text = runs[0].text.replacing(/^\s*(?:[•◦▪‣·\-–]|\d+[.)]?|[a-zA-Z][.)])\s+/, with: "")
            .trimmingCharacters(in: .whitespaces)
    }
}
