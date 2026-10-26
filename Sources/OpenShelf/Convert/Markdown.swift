import Foundation

/// A parsed Markdown document: CommonMark with GitHub's tables, task lists and
/// strikethrough, plus the Pandoc extensions people reach for most: footnotes,
/// definition lists, simple and multi-line tables, line blocks, math, smart
/// punctuation and a YAML title.
struct MarkdownDocument: Equatable {
    var title: String?
    var blocks: [MarkdownBlock<MarkdownText>]
    /// Footnotes in the order they're first referenced; `.footnote(n)` is
    /// `footnotes[n - 1]`.
    var footnotes: [[MarkdownBlock<MarkdownText>]]

    init(title: String? = nil, blocks: [MarkdownBlock<MarkdownText>], footnotes: [[MarkdownBlock<MarkdownText>]] = []) {
        self.title = title
        self.blocks = blocks
        self.footnotes = footnotes
    }

    init(parsing source: String) {
        self = MarkdownParser.parse(source)
    }
}

typealias MarkdownText = [MarkdownInline]

/// One block of a document. `Text` is the raw source while blocks are being
/// found, and inline content once they're done.
indirect enum MarkdownBlock<Text: Equatable>: Equatable {
    case heading(level: Int, text: Text, id: String)
    case paragraph(Text)
    case code(String, language: String?)
    /// Display math, as TeX source.
    case math(String)
    case quote([MarkdownBlock])
    case list(MarkdownList<Text>)
    case table(MarkdownTable<Text>)
    case definitions([MarkdownDefinition<Text>])
    /// A Pandoc line block: line breaks and leading spaces kept.
    case lines([Text])
    case rule
}

struct MarkdownList<Text: Equatable>: Equatable {
    var ordered: Bool
    var start = 1
    /// Items without blank lines between them, set close together.
    var tight = true
    var items: [[MarkdownBlock<Text>]]
}

enum MarkdownAlignment: Equatable {
    case none, left, center, right
}

struct MarkdownTable<Text: Equatable>: Equatable {
    var header: [Text]?
    var rows: [[Text]]
    var alignments: [MarkdownAlignment]
    var caption: Text?
    /// Each column's share of the width, for multi-line tables; nil to fit the contents.
    var widths: [Double]?

    var columnCount: Int { alignments.count }
}

struct MarkdownDefinition<Text: Equatable>: Equatable {
    var term: Text
    var definitions: [[MarkdownBlock<Text>]]
}

indirect enum MarkdownInline: Equatable {
    case text(String)
    case emphasis(MarkdownText)
    case strong(MarkdownText)
    case strikethrough(MarkdownText)
    case code(String)
    /// Inline math, as TeX source.
    case math(String)
    case link(url: String, title: String?, content: MarkdownText)
    case image(source: String, title: String?, alt: MarkdownText)
    /// A footnote reference, numbered from 1.
    case footnote(Int)
    case lineBreak
    case softBreak
}

extension MarkdownInline {
    /// The words of `text` without formatting, as a screen reader or a plain
    /// text file would have them.
    static func plainText(_ text: MarkdownText) -> String {
        text.map { inline in
            switch inline {
            case .text(let string), .code(let string): string
            case .math(let tex): MarkdownMath.unicode(tex)
            case .emphasis(let content), .strong(let content), .strikethrough(let content): plainText(content)
            case .link(_, _, let content): plainText(content)
            case .image(_, _, let alt): plainText(alt)
            case .footnote: ""
            case .lineBreak: "\n"
            case .softBreak: " "
            }
        }
        .joined()
    }

    /// A paragraph made of one image (and nothing else) is shown as a figure.
    static func figureImage(_ text: MarkdownText) -> (source: String, title: String?, alt: MarkdownText)? {
        guard text.count == 1, case .image(let source, let title, let alt) = text[0] else { return nil }
        return (source, title, alt)
    }
}

extension MarkdownBlock {
    /// The same block with `transform` applied to all its text, in document order.
    func map<T: Equatable>(_ transform: (Text) -> T) -> MarkdownBlock<T> {
        switch self {
        case .heading(let level, let text, let id): .heading(level: level, text: transform(text), id: id)
        case .paragraph(let text): .paragraph(transform(text))
        case .code(let code, let language): .code(code, language: language)
        case .math(let tex): .math(tex)
        case .quote(let blocks): .quote(blocks.map { $0.map(transform) })
        case .list(let list):
            .list(MarkdownList(ordered: list.ordered, start: list.start, tight: list.tight, items: list.items.map { $0.map { $0.map(transform) } }))
        case .table(let table):
            .table(MarkdownTable(
                header: table.header?.map(transform),
                rows: table.rows.map { $0.map(transform) },
                alignments: table.alignments,
                caption: table.caption.map(transform),
                widths: table.widths
            ))
        case .definitions(let definitions):
            .definitions(definitions.map { item in
                MarkdownDefinition(term: transform(item.term), definitions: item.definitions.map { $0.map { $0.map(transform) } })
            })
        case .lines(let lines): .lines(lines.map(transform))
        case .rule: .rule
        }
    }
}

// MARK: - Blocks

/// Finds the blocks of a Markdown document, then the inline content of each.
struct MarkdownParser {
    private var references: [String: (url: String, title: String?)] = [:]
    private var footnoteSources: [String: [MarkdownBlock<String>]] = [:]
    private var footnoteNumbers: [String: Int] = [:]
    private var footnotes: [[MarkdownBlock<MarkdownText>]] = []
    private var headingIDs: Set<String> = []

    static func parse(_ source: String) -> MarkdownDocument {
        var parser = MarkdownParser()
        var lines = source
            .replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n")
            .map(expandingLeadingTabs)
        let title = takeTitle(from: &lines)
        let raw = parser.blocks(in: lines[...])
        var blocks: [MarkdownBlock<MarkdownText>] = []
        for block in raw {
            blocks.append(parser.resolve(block))
        }
        return MarkdownDocument(title: title, blocks: blocks, footnotes: parser.footnotes)
    }

    // MARK: Front matter

    /// Removes a YAML block (`---` … `---`) or a Pandoc title block (`% Title`)
    /// from the top, returning its title.
    static func takeTitle(from lines: inout [String]) -> String? {
        if lines.first?.hasPrefix("% ") == true {
            let title = String(lines.removeFirst().dropFirst(2)).trimmingCharacters(in: .whitespaces)
            while lines.first?.hasPrefix("% ") == true { lines.removeFirst() }
            return title.isEmpty ? nil : title
        }
        guard lines.first?.trimmingCharacters(in: .whitespaces) == "---",
              let end = lines.dropFirst().firstIndex(where: { ["---", "..."].contains($0.trimmingCharacters(in: .whitespaces)) }),
              lines[1..<end].contains(where: { $0.firstMatch(of: /^[A-Za-z_][\w-]*:/) != nil })
        else { return nil }
        let title = lines[1..<end].lazy.compactMap { line -> String? in
            guard let match = line.firstMatch(of: /^title:\s*(.*?)\s*$/) else { return nil }
            var value = String(match.1)
            if value.count >= 2, let first = value.first, first == value.last, first == "\"" || first == "'" {
                value = String(value.dropFirst().dropLast())
            }
            return value
        }.first
        lines.removeSubrange(0...end)
        return title.flatMap { $0.isEmpty ? nil : $0 }
    }

    // MARK: Finding blocks

    private mutating func blocks(in lines: ArraySlice<String>) -> [MarkdownBlock<String>] {
        var blocks: [MarkdownBlock<String>] = []
        var index = lines.startIndex
        while index < lines.endIndex {
            let line = lines[index]
            if line.isBlank {
                index += 1
                continue
            }
            let indent = line.indentation
            if indent >= 4 {
                index = indentedCode(lines, from: index, into: &blocks)
                continue
            }
            let trimmed = String(line.dropFirst(indent))
            if let next = fencedCode(lines, from: index, into: &blocks)
                ?? displayMath(lines, from: index, into: &blocks)
                ?? multilineTable(lines, from: index, into: &blocks)
                ?? pipeTable(lines, from: index, into: &blocks)
                ?? simpleTable(lines, from: index, into: &blocks) {
                index = next
                continue
            }
            if let heading = Self.atxHeading(trimmed) {
                blocks.append(.heading(level: heading.level, text: heading.text, id: heading.id ?? ""))
                index += 1
            } else if Self.isRule(trimmed) {
                blocks.append(.rule)
                index += 1
            } else if trimmed.hasPrefix(">") {
                index = quote(lines, from: index, into: &blocks)
            } else if Self.listMarker(line) != nil {
                index = list(lines, from: index, into: &blocks)
            } else if let next = footnoteDefinition(lines, from: index) ?? referenceDefinition(lines, from: index) {
                index = next
            } else if trimmed == "|" || trimmed.hasPrefix("| ") {
                index = lineBlock(lines, from: index, into: &blocks)
            } else if trimmed.hasPrefix("<!--") {
                index = lines[index...].firstIndex { $0.contains("-->") }.map { $0 + 1 } ?? lines.endIndex
            } else if let next = definitionList(lines, from: index, into: &blocks) {
                index = next
            } else {
                index = paragraph(lines, from: index, into: &blocks)
            }
        }
        return attachingCaptions(blocks)
    }

    private func indentedCode(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int {
        var end = start
        while end < lines.endIndex, lines[end].isBlank || lines[end].indentation >= 4 { end += 1 }
        while end > start, lines[end - 1].isBlank { end -= 1 }
        let code = lines[start..<end].map { String($0.dropLeadingSpaces(4)) }.joined(separator: "\n")
        blocks.append(.code(code, language: nil))
        return end
    }

    private func fencedCode(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int? {
        let line = lines[start]
        let indent = line.indentation
        guard indent < 4, let match = line.dropFirst(indent).firstMatch(of: /^(`{3,}|~{3,})[ \t]*(.*)$/) else { return nil }
        let fence = String(match.1)
        let info = String(match.2).trimmingCharacters(in: .whitespaces)
        if fence.first == "`" && info.contains("`") { return nil }
        var end = start + 1
        var code: [String] = []
        while end < lines.endIndex {
            let candidate = lines[end].trimmingCharacters(in: .whitespaces)
            if lines[end].indentation < 4, candidate.count >= fence.count,
               candidate.allSatisfy({ $0 == fence.first }) {
                end += 1
                break
            }
            code.append(String(lines[end].dropLeadingSpaces(indent)))
            end += 1
        }
        blocks.append(.code(code.joined(separator: "\n"), language: Self.language(fromInfo: info)))
        return end
    }

    /// "python", "{.python}", "{.python .numberLines}" and "python title=x" all mean Python.
    static func language(fromInfo info: String) -> String? {
        let word = info.trimmingCharacters(in: CharacterSet(charactersIn: "{} "))
            .split(whereSeparator: { $0 == " " || $0 == "\t" }).first.map(String.init) ?? ""
        let language = word.hasPrefix(".") ? String(word.dropFirst()) : word
        return language.isEmpty || language.contains("=") || language.hasPrefix("#") ? nil : language
    }

    private func displayMath(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int? {
        let first = lines[start].trimmingCharacters(in: .whitespaces)
        guard first.hasPrefix("$$") else { return nil }
        let rest = first.dropFirst(2)
        if let close = rest.range(of: "$$") {
            // `$$x$$` on one line; text after it would be a paragraph, which inline math handles.
            guard rest[close.upperBound...].trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
            blocks.append(.math(String(rest[..<close.lowerBound]).trimmingCharacters(in: .whitespaces)))
            return start + 1
        }
        var tex = [String(rest)]
        var end = start + 1
        while end < lines.endIndex {
            if let close = lines[end].range(of: "$$") {
                tex.append(String(lines[end][..<close.lowerBound]))
                blocks.append(.math(tex.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)))
                return end + 1
            }
            if lines[end].isBlank { return nil }
            tex.append(lines[end])
            end += 1
        }
        return nil
    }

    static func atxHeading(_ line: String) -> (level: Int, text: String, id: String?)? {
        guard let match = line.firstMatch(of: /^(#{1,6})(?:[ \t]+(.*?))?[ \t]*$/) else { return nil }
        var text = String(match.2 ?? "")
        // Closing hashes, when separated by a space: "## Title ##".
        if let closing = text.firstMatch(of: /(?:^|[ \t]+)#+$/) {
            text.removeSubrange(closing.range)
        }
        var id: String?
        if let attributes = text.firstMatch(of: /[ \t]*\{([^}]*)\}$/) {
            id = attributes.1.split(separator: " ").first { $0.hasPrefix("#") }.map { String($0.dropFirst()) }
            text.removeSubrange(attributes.range)
        }
        return (match.1.count, text.trimmingCharacters(in: .whitespaces), id)
    }

    static func isRule(_ line: String) -> Bool {
        line.firstMatch(of: /^([-*_])(?:[ \t]*\1){2,}[ \t]*$/) != nil
    }

    private mutating func quote(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int {
        var content: [String] = []
        var end = start
        while end < lines.endIndex {
            let line = lines[end]
            let trimmed = line.drop { $0 == " " }
            if line.indentation < 4, trimmed.hasPrefix(">") {
                var rest = trimmed.dropFirst()
                if rest.first == " " { rest = rest.dropFirst() }
                content.append(String(rest))
            } else if !line.isBlank, let last = content.last, !last.isBlank, !Self.startsBlock(line) {
                // A lazy continuation of the quoted paragraph.
                content.append(line)
            } else {
                break
            }
            end += 1
        }
        blocks.append(.quote(self.blocks(in: content[...])))
        return end
    }

    // MARK: Lists

    struct ListMarker {
        var indent: Int
        var ordered: Bool
        /// "-", "*", "+" or the number's delimiter, "." or ")".
        var symbol: Character
        var number: Int
        /// Where the item's text starts, so continuation lines line up with it.
        var contentIndent: Int
        var content: String
    }

    static func listMarker(_ line: String) -> ListMarker? {
        let indent = line.indentation
        guard indent < 4 else { return nil }
        let rest = line.dropFirst(indent)
        var marker: (ordered: Bool, symbol: Character, number: Int, width: Int)
        if let match = rest.firstMatch(of: /^([-*+])(?=[ \t]|$)/) {
            marker = (false, match.1.first!, 1, 1)
        } else if let match = rest.firstMatch(of: /^(\d{1,9})([.)])(?=[ \t]|$)/) {
            marker = (true, match.2.first!, Int(match.1) ?? 1, match.0.count)
        } else {
            return nil
        }
        let afterMarker = rest.dropFirst(marker.width)
        let spaces = afterMarker.prefix { $0 == " " }.count
        let content = afterMarker.dropFirst(spaces)
        // Five or more spaces start indented code inside the item, one space in.
        let gap = content.isEmpty || spaces > 4 ? 1 : spaces
        return ListMarker(
            indent: indent,
            ordered: marker.ordered,
            symbol: marker.symbol,
            number: marker.number,
            contentIndent: indent + marker.width + gap,
            content: spaces > 4 ? String(repeating: " ", count: spaces - 1) + content : String(content)
        )
    }

    private mutating func list(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int {
        guard let first = Self.listMarker(lines[start]) else { return start + 1 }
        var items: [[String]] = []
        var current = [Self.taskBox(first.content)]
        var contentIndent = first.contentIndent
        var loose = false
        var end = start + 1
        while end < lines.endIndex {
            let line = lines[end]
            if line.isBlank {
                current.append("")
                end += 1
                continue
            }
            let previousWasBlank = current.last?.isBlank ?? false
            if line.indentation >= contentIndent {
                if previousWasBlank && current.contains(where: { !$0.isBlank }) && current.dropLast().last?.isBlank == false {
                    loose = true
                }
                current.append(String(line.dropLeadingSpaces(contentIndent)))
            } else if let marker = Self.listMarker(line), marker.ordered == first.ordered, marker.symbol == first.symbol || first.ordered {
                if previousWasBlank { loose = true }
                items.append(current)
                current = [Self.taskBox(marker.content)]
                contentIndent = marker.contentIndent
            } else if !previousWasBlank, !Self.startsBlock(line) {
                // Lazy continuation of the item's paragraph.
                current.append(line.trimmingCharacters(in: .whitespaces))
            } else {
                break
            }
            end += 1
        }
        items.append(current)
        // Blank lines after the last item belong to no one.
        while end > start, lines[end - 1].isBlank { end -= 1 }
        var parsedItems: [[MarkdownBlock<String>]] = []
        for item in items {
            var trimmedItem = item[...]
            while trimmedItem.last?.isBlank == true { trimmedItem = trimmedItem.dropLast() }
            // A blank line between two of the item's own blocks makes the list loose.
            if trimmedItem.dropFirst().contains(where: \.isBlank),
               let lastBlank = trimmedItem.lastIndex(where: \.isBlank),
               trimmedItem[lastBlank...].contains(where: { !$0.isBlank && $0.indentation < 4 && Self.listMarker($0) == nil }) {
                loose = true
            }
            parsedItems.append(self.blocks(in: trimmedItem))
        }
        blocks.append(.list(MarkdownList(ordered: first.ordered, start: first.number, tight: !loose, items: parsedItems)))
        return end
    }

    /// "[ ] Buy milk" and "[x] Done" become ballot boxes.
    static func taskBox(_ content: String) -> String {
        if let match = content.firstMatch(of: /^\[([ xX])\][ \t]+/) {
            return (match.1 == " " ? "☐ " : "☑ ") + content[match.range.upperBound...]
        }
        return content
    }

    // MARK: Definitions

    private mutating func footnoteDefinition(_ lines: ArraySlice<String>, from start: Int) -> Int? {
        guard let match = lines[start].firstMatch(of: /^ {0,3}\[\^([^\]\s]+)\]:[ \t]?(.*)$/) else { return nil }
        let label = String(match.1).lowercased()
        var content = [String(match.2)]
        var end = start + 1
        while end < lines.endIndex {
            let line = lines[end]
            if line.isBlank {
                content.append("")
            } else if line.indentation >= 4 {
                content.append(String(line.dropLeadingSpaces(4)))
            } else if content.last?.isBlank == false, !Self.startsBlock(line), !line.hasPrefix("[^") {
                content.append(line)
            } else {
                break
            }
            end += 1
        }
        while end > start + 1, lines[end - 1].isBlank { end -= 1 }
        if footnoteSources[label] == nil {
            footnoteSources[label] = blocks(in: content[...])
        }
        return end
    }

    private mutating func referenceDefinition(_ lines: ArraySlice<String>, from start: Int) -> Int? {
        let pattern = /^ {0,3}\[([^\]^][^\]]*)\]:[ \t]*<?([^\s>]+)>?(?:[ \t]+(?:"([^"]*)"|'([^']*)'|\(([^)]*)\)))?[ \t]*$/
        guard let match = lines[start].firstMatch(of: pattern) else { return nil }
        let label = Self.normalizedLabel(String(match.1))
        if references[label] == nil {
            let title = (match.3 ?? match.4 ?? match.5).map(String.init)
            references[label] = (String(match.2), title)
        }
        return start + 1
    }

    static func normalizedLabel(_ label: String) -> String {
        label.lowercased().split(whereSeparator: \.isWhitespace).joined(separator: " ")
    }

    private func lineBlock(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int {
        var result: [String] = []
        var end = start
        while end < lines.endIndex {
            let line = lines[end]
            let trimmed = line.drop { $0 == " " }
            if trimmed == "|" || trimmed.hasPrefix("| ") {
                let text = trimmed.dropFirst(trimmed.count > 1 ? 2 : 1)
                // Leading spaces are kept as no-break spaces, which every renderer keeps.
                let leading = text.prefix { $0 == " " }.count
                result.append(String(repeating: "\u{00A0}", count: leading) + text.dropFirst(leading))
            } else if !line.isBlank, line.hasPrefix(" "), !result.isEmpty {
                result[result.count - 1] += " " + line.trimmingCharacters(in: .whitespaces)
            } else {
                break
            }
            end += 1
        }
        blocks.append(.lines(result))
        return end
    }

    /// `Term` followed by `: definition` (or `~ definition`), optionally after one blank line.
    private mutating func definitionList(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int? {
        func definitionMarker(_ line: String) -> (content: String, indent: Int)? {
            guard let match = line.firstMatch(of: /^( {0,3})[:~]([ \t]+)(.*)$/) else { return nil }
            return (String(match.3), match.1.count + 1 + match.2.count)
        }
        func definitionStarts(after term: Int) -> Int? {
            if term + 1 < lines.endIndex, definitionMarker(lines[term + 1]) != nil { return term + 1 }
            if term + 2 < lines.endIndex, lines[term + 1].isBlank, definitionMarker(lines[term + 2]) != nil { return term + 2 }
            return nil
        }
        guard definitionStarts(after: start) != nil else { return nil }

        var items: [MarkdownDefinition<String>] = []
        var index = start
        while index < lines.endIndex, !lines[index].isBlank, let first = definitionStarts(after: index) {
            let term = lines[index].trimmingCharacters(in: .whitespaces)
            var definitions: [[MarkdownBlock<String>]] = []
            var position = first
            while position < lines.endIndex, let marker = definitionMarker(lines[position]) {
                var content = [marker.content]
                var end = position + 1
                while end < lines.endIndex {
                    let line = lines[end]
                    if line.isBlank {
                        // Continues only when indented text or another definition follows.
                        guard end + 1 < lines.endIndex, lines[end + 1].indentation >= 2 || definitionMarker(lines[end + 1]) != nil else { break }
                        if definitionMarker(lines[end + 1]) != nil { break }
                        content.append("")
                    } else if definitionMarker(line) != nil {
                        break
                    } else if line.indentation >= 2 {
                        content.append(String(line.dropLeadingSpaces(min(marker.indent, 4))))
                    } else if content.last?.isBlank == false, !Self.startsBlock(line), definitionStarts(after: end) == nil {
                        // Lazy continuation, unless the line is the next term.
                        content.append(line)
                    } else {
                        break
                    }
                    end += 1
                }
                definitions.append(self.blocks(in: content[...]))
                position = end
                if position + 1 < lines.endIndex, lines[position].isBlank, definitionMarker(lines[position + 1]) != nil {
                    position += 1
                }
            }
            items.append(MarkdownDefinition(term: term, definitions: definitions))
            index = position
            // Terms may be separated by one blank line.
            if index + 1 < lines.endIndex, lines[index].isBlank, !lines[index + 1].isBlank, definitionStarts(after: index + 1) != nil {
                index += 1
            }
        }
        blocks.append(.definitions(items))
        return index
    }

    // MARK: Paragraphs

    private func paragraph(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int {
        var text: [String] = [lines[start].trimmingCharacters(in: .whitespaces)]
        var end = start + 1
        while end < lines.endIndex {
            let line = lines[end]
            if line.isBlank { break }
            if line.indentation < 4, let match = line.firstMatch(of: /^ {0,3}(=+|-+)[ \t]*$/) {
                let raw = text.joined(separator: "\n")
                let heading = Self.atxHeading("# " + raw)
                blocks.append(.heading(level: match.1.first == "=" ? 1 : 2, text: heading?.text ?? raw, id: heading?.id ?? ""))
                return end + 1
            }
            if Self.startsBlock(line) { break }
            // Keep the two trailing spaces of a hard line break.
            text.append(line.hasSuffix("  ") ? String(line.drop { $0 == " " }) : line.trimmingCharacters(in: .whitespaces))
            end += 1
        }
        // A trailing hard break means nothing at the end of a paragraph.
        if let last = text.last, last.hasSuffix(" ") {
            text[text.count - 1] = last.trimmingCharacters(in: .whitespaces)
        }
        blocks.append(.paragraph(text.joined(separator: "\n")))
        return end
    }

    /// Lines that end a paragraph without a blank line before them.
    static func startsBlock(_ line: String) -> Bool {
        guard line.indentation < 4 else { return false }
        let trimmed = String(line.drop { $0 == " " })
        if trimmed.hasPrefix(">") || trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") || trimmed.hasPrefix("$$") { return true }
        if atxHeading(trimmed) != nil || isRule(trimmed) { return true }
        if let marker = listMarker(line), !marker.content.isEmpty {
            return !marker.ordered || marker.number == 1
        }
        return false
    }

    // MARK: Tables

    /// GitHub's pipe tables.
    private func pipeTable(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int? {
        guard start + 1 < lines.endIndex, lines[start].contains("|") else { return nil }
        let delimiter = lines[start + 1]
        guard delimiter.contains("|") || lines[start].trimmingCharacters(in: .whitespaces).hasPrefix("|"),
              delimiter.firstMatch(of: /^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$/) != nil
        else { return nil }
        let alignments: [MarkdownAlignment] = Self.pipeCells(delimiter).map { cell in
            switch (cell.hasPrefix(":"), cell.hasSuffix(":")) {
            case (true, true): .center
            case (true, false): .left
            case (false, true): .right
            case (false, false): .none
            }
        }
        let header = Self.pipeCells(lines[start])
        guard header.count == alignments.count else { return nil }
        var rows: [[String]] = []
        var end = start + 2
        while end < lines.endIndex, !lines[end].isBlank, lines[end].contains("|") || !Self.startsBlock(lines[end]) {
            guard lines[end].contains("|") else { break }
            rows.append(Self.fitted(Self.pipeCells(lines[end]), to: alignments.count))
            end += 1
        }
        let table = MarkdownTable(header: header, rows: rows, alignments: alignments)
        return caption(for: table, lines, after: end, into: &blocks)
    }

    /// Splits a pipe-table row at `|`, except escaped ones and those inside code.
    static func pipeCells(_ line: String) -> [String] {
        var row = line.trimmingCharacters(in: .whitespaces)
        if row.hasPrefix("|") { row.removeFirst() }
        if row.hasSuffix("|") && !row.hasSuffix("\\|") { row.removeLast() }
        var cells: [String] = []
        var cell = ""
        var inCode = false
        var escaped = false
        for character in row {
            if escaped {
                // `\|` is a literal pipe; other escapes are left for the inline parser.
                if character != "|" { cell.append("\\") }
                cell.append(character)
                escaped = false
            } else if character == "\\" {
                escaped = true
            } else if character == "`" {
                inCode.toggle()
                cell.append(character)
            } else if character == "|" && !inCode {
                cells.append(cell.trimmingCharacters(in: .whitespaces))
                cell = ""
            } else {
                cell.append(character)
            }
        }
        if escaped { cell.append("\\") }
        cells.append(cell.trimmingCharacters(in: .whitespaces))
        return cells
    }

    private static func fitted(_ cells: [String], to count: Int) -> [String] {
        cells.count >= count ? Array(cells.prefix(count)) : cells + Array(repeating: "", count: count - cells.count)
    }

    /// The column spans of a line of dashes such as `----  -------`.
    static func dashColumns(_ line: String) -> [Range<Int>]? {
        let characters = Array(line)
        guard characters.allSatisfy({ $0 == "-" || $0 == " " }), characters.contains("-") else { return nil }
        var columns: [Range<Int>] = []
        var index = 0
        while index < characters.count {
            if characters[index] == "-" {
                let start = index
                while index < characters.count, characters[index] == "-" { index += 1 }
                columns.append(start..<index)
            } else {
                index += 1
            }
        }
        return columns
    }

    /// Pandoc's simple tables: a header line over dashes, one per column.
    private func simpleTable(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int? {
        guard start + 1 < lines.endIndex, !lines[start].isBlank,
              let columns = Self.dashColumns(lines[start + 1]), columns.count >= 2
        else { return nil }
        let headerLine = lines[start]
        let header = Self.cells(of: headerLine, in: columns)
        let alignments = columns.indices.map { Self.alignment(of: headerLine, in: columns, column: $0) }
        var rows: [[String]] = []
        var end = start + 2
        while end < lines.endIndex, !lines[end].isBlank, Self.dashColumns(lines[end]) == nil {
            rows.append(Self.cells(of: lines[end], in: columns))
            end += 1
        }
        // A closing line of dashes is optional.
        if end < lines.endIndex, Self.dashColumns(lines[end]) != nil { end += 1 }
        guard !rows.isEmpty else { return nil }
        let table = MarkdownTable(header: header, rows: rows, alignments: alignments)
        return caption(for: table, lines, after: end, into: &blocks)
    }

    /// Pandoc's multi-line tables: dashes above and below, rows separated by blank lines.
    private func multilineTable(_ lines: ArraySlice<String>, from start: Int, into blocks: inout [MarkdownBlock<String>]) -> Int? {
        guard let top = Self.dashColumns(lines[start]), top.first.map({ $0.count >= 3 }) == true,
              start + 1 < lines.endIndex, !lines[start + 1].isBlank, Self.dashColumns(lines[start + 1]) == nil
        else { return nil }
        // Header lines, then the dashes that set the columns.
        var separator = start + 1
        while separator < lines.endIndex, !lines[separator].isBlank, Self.dashColumns(lines[separator]) == nil { separator += 1 }
        guard separator < lines.endIndex, let columns = Self.dashColumns(lines[separator]), columns.count >= 2 else { return nil }
        // The closing dashes must be followed by a blank line or the end.
        var close = separator + 1
        while close < lines.endIndex, Self.dashColumns(lines[close]) == nil { close += 1 }
        guard close < lines.endIndex, close + 1 == lines.endIndex || lines[close + 1].isBlank || lines[close + 1].hasPrefix("Table:") || lines[close + 1].hasPrefix(":") else {
            return nil
        }
        func joinedCells(_ rowLines: ArraySlice<String>) -> [String] {
            let pieces = rowLines.map { Self.cells(of: $0, in: columns) }
            return columns.indices.map { column in
                pieces.map { $0[column] }.filter { !$0.isEmpty }.joined(separator: "\n")
            }
        }
        let headerLines = lines[(start + 1)..<separator]
        let header = joinedCells(headerLines)
        let alignments = columns.indices.map { Self.alignment(of: headerLines.first ?? "", in: columns, column: $0) }
        var rows: [[String]] = []
        var row: [String] = []
        for line in lines[(separator + 1)..<close] {
            if line.isBlank {
                if !row.isEmpty { rows.append(joinedCells(row[...])) }
                row = []
            } else {
                row.append(line)
            }
        }
        if !row.isEmpty { rows.append(joinedCells(row[...])) }
        let total = Double(columns.map(\.count).reduce(0, +))
        let table = MarkdownTable(
            header: header,
            rows: rows,
            alignments: alignments,
            widths: columns.map { Double($0.count) / max(total, 1) }
        )
        return caption(for: table, lines, after: close + 1, into: &blocks)
    }

    /// The text of each column of `line`, cut where the next column's dashes begin.
    static func cells(of line: String, in columns: [Range<Int>]) -> [String] {
        let characters = Array(line)
        return columns.indices.map { index in
            let start = index == 0 ? 0 : columns[index].lowerBound
            let end = index + 1 < columns.count ? columns[index + 1].lowerBound : characters.count
            guard start < characters.count, start < end else { return "" }
            return String(characters[start..<min(end, characters.count)]).trimmingCharacters(in: .whitespaces)
        }
    }

    /// How a simple table's header sits over its dashes sets the column's alignment.
    static func alignment(of header: String, in columns: [Range<Int>], column: Int) -> MarkdownAlignment {
        let characters = Array(header)
        let span = columns[column]
        guard span.lowerBound < characters.count else { return .none }
        let end = min(span.upperBound, characters.count)
        let text = characters[span.lowerBound..<end]
        let flushLeft = text.first.map { $0 != " " } ?? false
        let flushRight = span.upperBound <= characters.count && characters[span.upperBound - 1] != " "
        switch (flushLeft, flushRight) {
        case (true, true): return .none
        case (true, false): return .left
        case (false, true): return .right
        case (false, false): return text.contains { $0 != " " } ? .center : .none
        }
    }

    /// Takes a `Table: caption` (or `: caption`) after the table, if there is one.
    private func caption(for table: MarkdownTable<String>, _ lines: ArraySlice<String>, after end: Int, into blocks: inout [MarkdownBlock<String>]) -> Int {
        var table = table
        var next = end
        var line = next
        if line < lines.endIndex, lines[line].isBlank { line += 1 }
        if line < lines.endIndex, let match = lines[line].firstMatch(of: /^ {0,3}(?:Table)?:[ \t]+(.*)$/) {
            var caption = [String(match.1)]
            next = line + 1
            while next < lines.endIndex, !lines[next].isBlank {
                caption.append(lines[next].trimmingCharacters(in: .whitespaces))
                next += 1
            }
            table.caption = caption.joined(separator: " ")
        }
        blocks.append(.table(table))
        return next
    }

    /// A `Table: caption` paragraph right before a table belongs to it.
    private func attachingCaptions(_ blocks: [MarkdownBlock<String>]) -> [MarkdownBlock<String>] {
        var result: [MarkdownBlock<String>] = []
        for block in blocks {
            if case .table(var table) = block, table.caption == nil,
               case .paragraph(let text) = result.last, let match = text.firstMatch(of: /^(?:Table)?:[ \t]+((?s).*)$/) {
                table.caption = String(match.1).replacingOccurrences(of: "\n", with: " ")
                result[result.count - 1] = .table(table)
            } else {
                result.append(block)
            }
        }
        return result
    }

    // MARK: Inline content

    private mutating func resolve(_ block: MarkdownBlock<String>) -> MarkdownBlock<MarkdownText> {
        switch block {
        case .heading(let level, let source, let explicitID):
            let text = inlines(source)
            let id = uniqueID(explicitID.isEmpty ? Self.identifier(for: MarkdownInline.plainText(text)) : explicitID)
            return .heading(level: level, text: text, id: id)
        case .quote(let blocks):
            return .quote(blocks.map { resolve($0) })
        case .list(let list):
            return .list(MarkdownList(ordered: list.ordered, start: list.start, tight: list.tight, items: list.items.map { $0.map { resolve($0) } }))
        case .definitions(let items):
            return .definitions(items.map { item in
                MarkdownDefinition(term: inlines(item.term), definitions: item.definitions.map { $0.map { resolve($0) } })
            })
        default:
            // Leaf blocks: their text is inline content.
            return block.map { inlines($0) }
        }
    }

    private mutating func uniqueID(_ base: String) -> String {
        let base = base.isEmpty ? "section" : base
        var id = base
        var suffix = 1
        while headingIDs.contains(id) {
            id = "\(base)-\(suffix)"
            suffix += 1
        }
        headingIDs.insert(id)
        return id
    }

    /// Pandoc's identifiers: "An h2 header" becomes "an-h2-header".
    static func identifier(for text: String) -> String {
        var id = ""
        for character in text.lowercased() {
            if character.isLetter || character.isNumber || "_-.".contains(character) {
                id.append(character)
            } else if character.isWhitespace {
                id.append("-")
            }
        }
        return String(id.drop { !$0.isLetter })
    }

    mutating func inlines(_ source: String) -> MarkdownText {
        var scanner = InlineScanner(source, resolver: self)
        let result = scanner.parse()
        self = scanner.resolver
        return result
    }

    fileprivate func reference(_ label: String) -> (url: String, title: String?)? {
        references[Self.normalizedLabel(label)]
    }

    /// The number of the footnote called `label`, numbering it on first use.
    fileprivate mutating func footnoteNumber(_ label: String) -> Int? {
        let key = label.lowercased()
        if let number = footnoteNumbers[key] { return number }
        guard let source = footnoteSources[key] else { return nil }
        return addFootnote(source, key: key)
    }

    /// A Pandoc inline footnote, `^[text]`.
    fileprivate mutating func inlineFootnote(_ text: String) -> Int {
        addFootnote([.paragraph(text)], key: nil)
    }

    private mutating func addFootnote(_ source: [MarkdownBlock<String>], key: String?) -> Int {
        footnotes.append([])
        let number = footnotes.count
        if let key { footnoteNumbers[key] = number }
        footnotes[number - 1] = source.map { resolve($0) }
        return number
    }
}

// MARK: - Inline content

/// Turns a block's text into emphasis, links, code, math and so on.
private struct InlineScanner {
    var resolver: MarkdownParser
    private let characters: [Character]
    private var position = 0
    private var pieces: [Piece] = []
    private var text = ""

    fileprivate enum Piece {
        case node(MarkdownInline)
        case delimiter(Delimiter)
    }

    fileprivate struct Delimiter {
        var character: Character
        var count: Int
        var canOpen: Bool
        var canClose: Bool
        let original: Int
    }

    init(_ source: String, resolver: MarkdownParser) {
        characters = Array(source)
        self.resolver = resolver
    }

    mutating func parse() -> MarkdownText {
        while position < characters.count {
            let character = characters[position]
            switch character {
            case "\\": backslash()
            case "`": codeSpan()
            case "$": math()
            case "!" where peek(1) == "[": if !link(image: true) { literal() }
            case "[": if !footnoteReference(), !link(image: false) { literal() }
            case "^" where peek(1) == "[": if !inlineFootnote() { literal() }
            case "<": angleBracket()
            case "*", "_", "~": delimiterRun()
            case "\n": lineEnd()
            case "&": entity()
            case "h", "w": if !bareURL() { smartText() }
            default: smartText()
            }
        }
        flush()
        processEmphasis(from: 0)
        return Self.merged(pieces.map(\.inline))
    }

    private func peek(_ offset: Int) -> Character? {
        let index = position + offset
        return index < characters.count && index >= 0 ? characters[index] : nil
    }

    private mutating func flush() {
        guard !text.isEmpty else { return }
        pieces.append(.node(.text(text)))
        text = ""
    }

    private mutating func append(_ inline: MarkdownInline) {
        flush()
        pieces.append(.node(inline))
    }

    private mutating func literal() {
        text.append(characters[position])
        position += 1
    }

    private mutating func backslash() {
        guard let next = peek(1) else {
            literal()
            return
        }
        if next == "\n" {
            append(.lineBreak)
            position += 2
        } else if next.isASCII, next.isPunctuation || next.isSymbol {
            text.append(next)
            position += 2
        } else {
            literal()
        }
    }

    private mutating func codeSpan() {
        let start = position
        var ticks = 0
        while peek(ticks) == "`" { ticks += 1 }
        var search = position + ticks
        while search < characters.count {
            if characters[search] == "`" {
                var run = 0
                while search + run < characters.count, characters[search + run] == "`" { run += 1 }
                if run == ticks {
                    var code = String(characters[(start + ticks)..<search]).replacingOccurrences(of: "\n", with: " ")
                    if code.count >= 2, code.hasPrefix(" "), code.hasSuffix(" "), code.contains(where: { $0 != " " }) {
                        code = String(code.dropFirst().dropLast())
                    }
                    append(.code(code))
                    position = search + run
                    // Pandoc's `code`{.language} attributes add nothing here.
                    skipAttributes()
                    return
                }
                search += run
            } else {
                search += 1
            }
        }
        text += String(repeating: "`", count: ticks)
        position += ticks
    }

    /// `$x$` and `$$x$$`. A `$` before a space or a digit after the closing one
    /// means money, not math.
    private mutating func math() {
        if peek(1) == "$" {
            var search = position + 2
            while search + 1 < characters.count {
                if characters[search] == "$" && characters[search + 1] == "$" {
                    let tex = String(characters[(position + 2)..<search]).trimmingCharacters(in: .whitespacesAndNewlines)
                    guard !tex.isEmpty else { break }
                    append(.math(tex))
                    position = search + 2
                    return
                }
                search += 1
            }
            text += "$$"
            position += 2
            return
        }
        guard let next = peek(1), !next.isWhitespace else {
            literal()
            return
        }
        var search = position + 1
        while search < characters.count {
            let character = characters[search]
            if character == "\\" {
                search += 2
                continue
            }
            if character == "\n", search + 1 < characters.count, characters[search + 1] == "\n" { break }
            if character == "$" {
                let before = characters[search - 1]
                let after = search + 1 < characters.count ? characters[search + 1] : " "
                if !before.isWhitespace, !after.isNumber {
                    append(.math(String(characters[(position + 1)..<search])))
                    position = search + 1
                    return
                }
            }
            search += 1
        }
        literal()
    }

    private mutating func footnoteReference() -> Bool {
        guard peek(1) == "^", let close = characters[position...].firstIndex(of: "]") else { return false }
        let label = String(characters[(position + 2)..<close])
        guard !label.isEmpty, !label.contains(where: \.isWhitespace), let number = resolver.footnoteNumber(label) else { return false }
        append(.footnote(number))
        position = close + 1
        return true
    }

    private mutating func inlineFootnote() -> Bool {
        guard let close = matchingBracket(from: position + 1) else { return false }
        let content = String(characters[(position + 2)..<close])
        append(.footnote(resolver.inlineFootnote(content)))
        position = close + 1
        return true
    }

    /// The index of the `]` that closes the `[` at `open`, skipping code and escapes.
    private func matchingBracket(from open: Int) -> Int? {
        var depth = 0
        var index = open
        while index < characters.count {
            switch characters[index] {
            case "\\": index += 1
            case "`":
                var ticks = 0
                while index + ticks < characters.count, characters[index + ticks] == "`" { ticks += 1 }
                let closing = String(repeating: "`", count: ticks)
                let rest = String(characters[(index + ticks)...])
                if let range = rest.range(of: closing) {
                    index += ticks + rest.distance(from: rest.startIndex, to: range.upperBound) - 1
                } else {
                    index += ticks - 1
                }
            case "[": depth += 1
            case "]":
                depth -= 1
                if depth == 0 { return index }
            default: break
            }
            index += 1
        }
        return nil
    }

    /// `[text](url "title")`, `[text][ref]`, `[ref]` and their image forms.
    private mutating func link(image: Bool) -> Bool {
        let open = image ? position + 1 : position
        guard let close = matchingBracket(from: open) else { return false }
        let label = String(characters[(open + 1)..<close])
        var after = close + 1
        var destination: (url: String, title: String?)?

        if after < characters.count, characters[after] == "(", let inline = inlineDestination(from: after) {
            destination = (inline.url, inline.title)
            after = inline.end
        } else if after < characters.count, characters[after] == "[", let refClose = characters[after...].firstIndex(of: "]") {
            let reference = String(characters[(after + 1)..<refClose])
            if let found = resolver.reference(reference.isEmpty ? label : reference) {
                destination = found
                after = refClose + 1
            }
        } else if let found = resolver.reference(label) {
            destination = found
        } else if !image, after < characters.count, characters[after] == "{" {
            // A Pandoc span, [text]{.class}: keep the text, drop the attributes.
            flush()
            let content = nested(label)
            content.forEach { pieces.append(.node($0)) }
            position = after
            skipAttributes()
            return true
        }
        guard let destination else { return false }
        let content = nested(label)
        append(image
            ? .image(source: destination.url, title: destination.title, alt: content)
            : .link(url: destination.url, title: destination.title, content: content))
        position = after
        skipAttributes()
        return true
    }

    private mutating func nested(_ source: String) -> MarkdownText {
        var scanner = InlineScanner(source, resolver: resolver)
        let content = scanner.parse()
        resolver = scanner.resolver
        return content
    }

    private func inlineDestination(from open: Int) -> (url: String, title: String?, end: Int)? {
        var index = open + 1
        func skipSpace() { while index < characters.count, characters[index].isWhitespace { index += 1 } }
        skipSpace()
        var url = ""
        if index < characters.count, characters[index] == "<" {
            guard let close = characters[index...].firstIndex(of: ">") else { return nil }
            url = String(characters[(index + 1)..<close])
            index = close + 1
        } else {
            var depth = 0
            while index < characters.count {
                let character = characters[index]
                if character.isWhitespace { break }
                if character == "\\", index + 1 < characters.count {
                    url.append(characters[index + 1])
                    index += 2
                    continue
                }
                if character == "(" { depth += 1 }
                if character == ")" {
                    if depth == 0 { break }
                    depth -= 1
                }
                url.append(character)
                index += 1
            }
        }
        skipSpace()
        var title: String?
        if index < characters.count, let closer: Character = ["\"": "\"", "'": "'", "(": ")"][characters[index]] {
            guard let close = characters[(index + 1)...].firstIndex(of: closer) else { return nil }
            title = String(characters[(index + 1)..<close])
            index = close + 1
            skipSpace()
        }
        guard index < characters.count, characters[index] == ")" else { return nil }
        return (url, title, index + 1)
    }

    /// Skips Pandoc attributes, `{width=50%}` or `{.class}`, after a link, image or code.
    private mutating func skipAttributes() {
        guard peek(0) == "{", let close = characters[position...].firstIndex(of: "}"),
              !characters[position..<close].contains("\n")
        else { return }
        position = close + 1
    }

    private mutating func angleBracket() {
        let rest = String(characters[position...])
        if let match = rest.prefixMatch(of: /<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*)>/) {
            append(.link(url: String(match.1), title: nil, content: [.text(String(match.1))]))
            position += match.0.count
        } else if let match = rest.prefixMatch(of: /<([A-Za-z0-9.!#$%&'*+\/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*)>/) {
            append(.link(url: "mailto:" + match.1, title: nil, content: [.text(String(match.1))]))
            position += match.0.count
        } else if let match = rest.prefixMatch(of: /<!--(?s).*?-->/) {
            position += match.0.count
        } else if let match = rest.prefixMatch(of: /<\/?([A-Za-z][A-Za-z0-9-]*)(?:\s[^<>]*)?\/?>/) {
            // Raw HTML tags are dropped and their text kept; <br> still breaks the line.
            if match.1.lowercased() == "br" { append(.lineBreak) }
            position += match.0.count
        } else {
            literal()
        }
    }

    /// A URL typed without angle brackets, as GitHub links them.
    private mutating func bareURL() -> Bool {
        if let previous = peek(-1), !(previous.isWhitespace || "(*_".contains(previous)) { return false }
        let rest = String(characters[position...])
        guard let match = rest.prefixMatch(of: /(?:https?:\/\/|www\.)[^\s<]+/) else { return false }
        var url = String(match.0)
        // Trailing punctuation ends the sentence, not the URL; so does an unmatched ")".
        while let last = url.last, ".,:;!?'\"*_".contains(last) || (last == ")" && url.filter({ $0 == "(" }).count < url.filter({ $0 == ")" }).count) {
            url.removeLast()
        }
        guard url.count > 4, url.contains(".") else { return false }
        append(.link(url: url.hasPrefix("www.") ? "http://" + url : url, title: nil, content: [.text(url)]))
        position += url.count
        return true
    }

    private mutating func entity() {
        let rest = String(characters[position...])
        if let match = rest.prefixMatch(of: /&(#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6}|[A-Za-z]{2,8});/) {
            let name = String(match.1)
            var decoded: String?
            if name.hasPrefix("#x") || name.hasPrefix("#X") {
                decoded = UInt32(name.dropFirst(2), radix: 16).flatMap(Unicode.Scalar.init).map { String(Character($0)) }
            } else if name.hasPrefix("#") {
                decoded = UInt32(name.dropFirst()).flatMap(Unicode.Scalar.init).map { String(Character($0)) }
            } else {
                decoded = Self.entities[name]
            }
            if let decoded {
                text += decoded
                position += match.0.count
                return
            }
        }
        literal()
    }

    private static let entities: [String: String] = [
        "amp": "&", "lt": "<", "gt": ">", "quot": "\"", "apos": "'", "nbsp": "\u{00A0}",
        "copy": "©", "reg": "®", "trade": "™", "mdash": "—", "ndash": "–", "hellip": "…",
        "laquo": "«", "raquo": "»", "ldquo": "“", "rdquo": "”", "lsquo": "‘", "rsquo": "’",
        "times": "×", "divide": "÷", "deg": "°", "middot": "·", "bull": "•", "rarr": "→", "larr": "←",
        "para": "¶", "sect": "§", "euro": "€", "pound": "£", "yen": "¥", "cent": "¢",
    ]

    private mutating func lineEnd() {
        // Two spaces at the end of a line make a hard break.
        if text.hasSuffix("  ") {
            text = String(text.reversed().drop { $0 == " " }.reversed())
            append(.lineBreak)
        } else {
            text = String(text.reversed().drop { $0 == " " }.reversed())
            append(.softBreak)
        }
        position += 1
        while peek(0) == " " { position += 1 }
    }

    /// Plain text, with straight quotes curled and `--`, `---` and `...` typeset.
    private mutating func smartText() {
        let character = characters[position]
        switch character {
        case "-" where peek(1) == "-":
            if peek(2) == "-" {
                text.append("—")
                position += 3
            } else {
                text.append("–")
                position += 2
            }
        case "." where peek(1) == "." && peek(2) == ".":
            text.append("…")
            position += 3
        case "\"":
            text.append(opensQuote() ? "“" : "”")
            position += 1
        case "'":
            if let previous = peek(-1), previous.isLetter || previous.isNumber {
                text.append("’")
            } else {
                text.append(opensQuote() ? "‘" : "’")
            }
            position += 1
        default:
            literal()
        }
    }

    private func opensQuote() -> Bool {
        let previous = peek(-1)
        let next = peek(1)
        let afterOpening = previous.map { $0.isWhitespace || "([{—–-/“‘".contains($0) } ?? true
        let beforeText = next.map { !$0.isWhitespace } ?? false
        return afterOpening && beforeText
    }

    // MARK: Emphasis

    private mutating func delimiterRun() {
        let character = characters[position]
        var count = 0
        while peek(count) == character { count += 1 }
        if character == "~" && count != 2 {
            text += String(repeating: "~", count: count)
            position += count
            return
        }
        let before = peek(-1) ?? " "
        let after = peek(count) ?? " "
        let leftFlanking = !after.isWhitespace && (!Self.isPunctuation(after) || before.isWhitespace || Self.isPunctuation(before))
        let rightFlanking = !before.isWhitespace && (!Self.isPunctuation(before) || after.isWhitespace || Self.isPunctuation(after))
        var canOpen = leftFlanking
        var canClose = rightFlanking
        if character == "_" {
            canOpen = leftFlanking && (!rightFlanking || Self.isPunctuation(before))
            canClose = rightFlanking && (!leftFlanking || Self.isPunctuation(after))
        }
        flush()
        pieces.append(.delimiter(Delimiter(character: character, count: count, canOpen: canOpen, canClose: canClose, original: count)))
        position += count
    }

    private static func isPunctuation(_ character: Character) -> Bool {
        character.isPunctuation || character.isSymbol
    }

    /// CommonMark's emphasis algorithm: pairs closers with the nearest
    /// matching opener before them.
    private mutating func processEmphasis(from bottom: Int) {
        var closer = bottom
        while closer < pieces.count {
            guard case .delimiter(var close) = pieces[closer], close.canClose else {
                closer += 1
                continue
            }
            var opener = closer - 1
            var found = false
            while opener >= bottom {
                if case .delimiter(let open) = pieces[opener], open.character == close.character, open.canOpen {
                    // The "multiple of 3" rule keeps ***a** b* sensible.
                    let oddMatch = (open.canClose || close.canOpen) && (open.original + close.original) % 3 == 0
                        && !(open.original % 3 == 0 && close.original % 3 == 0)
                    if !oddMatch {
                        found = true
                        break
                    }
                }
                opener -= 1
            }
            guard found, case .delimiter(var open) = pieces[opener] else {
                if !close.canOpen { pieces[closer] = .node(.text(String(repeating: close.character, count: close.count))) }
                closer += 1
                continue
            }
            let used = close.character == "~" ? 2 : (open.count >= 2 && close.count >= 2 ? 2 : 1)
            let inner = Self.merged(pieces[(opener + 1)..<closer].map(\.inline))
            let wrapped: MarkdownInline = switch (close.character, used) {
            case ("~", _): .strikethrough(inner)
            case (_, 2): .strong(inner)
            default: .emphasis(inner)
            }
            open.count -= used
            close.count -= used
            pieces.replaceSubrange((opener + 1)..<closer, with: [.node(wrapped)])
            closer = opener + 2
            if open.count == 0 {
                pieces.remove(at: opener)
                closer -= 1
            } else {
                pieces[opener] = .delimiter(open)
            }
            if close.count == 0 {
                pieces.remove(at: closer)
            } else {
                pieces[closer] = .delimiter(close)
            }
        }
    }

    /// Joins neighbouring text and leaves unmatched delimiters as text.
    static func merged(_ inlines: [MarkdownInline]) -> MarkdownText {
        var result: MarkdownText = []
        for inline in inlines {
            if case .text(let next) = inline, case .text(let previous) = result.last {
                result[result.count - 1] = .text(previous + next)
            } else if case .text(let string) = inline, string.isEmpty {
                continue
            } else {
                result.append(inline)
            }
        }
        return result
    }
}

private extension InlineScanner.Piece {
    var inline: MarkdownInline {
        switch self {
        case .node(let inline): inline
        case .delimiter(let delimiter): .text(String(repeating: delimiter.character, count: delimiter.count))
        }
    }
}

// MARK: - Helpers

private func expandingLeadingTabs(_ line: String) -> String {
    guard line.contains("\t") else { return line }
    var result = ""
    var column = 0
    var leading = true
    for character in line {
        if leading && character == "\t" {
            let spaces = 4 - column % 4
            result += String(repeating: " ", count: spaces)
            column += spaces
        } else {
            if character != " " { leading = false }
            result.append(character)
            column += 1
        }
    }
    return result
}

private extension StringProtocol {
    var isBlank: Bool { allSatisfy { $0 == " " || $0 == "\t" } }
    var indentation: Int { prefix { $0 == " " }.count }

    /// Removes up to `count` leading spaces.
    func dropLeadingSpaces(_ count: Int) -> SubSequence {
        dropFirst(Swift.min(count, prefix { $0 == " " }.count))
    }
}
