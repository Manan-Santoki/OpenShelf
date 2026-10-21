import Foundation

/// Finder-style type-to-select: letters typed in quick succession build a
/// prefix, and the first item whose name starts with it gets selected.
struct TypeSelect {
    /// Typing after this long a pause starts a new prefix.
    var timeout: TimeInterval = 1
    private(set) var prefix = ""
    private var lastInput: TimeInterval?

    /// Adds typed characters and returns the prefix to match.
    mutating func append(_ characters: String, at time: TimeInterval) -> String {
        if let lastInput, time - lastInput > timeout {
            prefix = ""
        }
        prefix += characters
        lastInput = time
        return prefix
    }

    /// Index of the first name starting with `prefix`, ignoring case and accents.
    static func match(_ prefix: String, in names: [String]) -> Int? {
        guard !prefix.isEmpty else { return nil }
        let options: String.CompareOptions = [.caseInsensitive, .diacriticInsensitive, .anchored]
        return names.firstIndex { $0.range(of: prefix, options: options) != nil }
    }

    /// Whether a key press should feed type-select rather than act as a shortcut.
    static func isTypingCharacter(_ characters: String) -> Bool {
        guard let scalar = characters.unicodeScalars.first, characters.unicodeScalars.count == 1 else { return false }
        return CharacterSet.alphanumerics.union(.punctuationCharacters).contains(scalar)
    }
}
