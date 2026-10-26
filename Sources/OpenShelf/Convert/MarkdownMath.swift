import Foundation

/// Shows TeX math as readable Unicode text: `\omega = d\phi / dt` becomes
/// "ω = dφ / dt" and `R^{2}` becomes "R²". It's no typesetter, but formulas
/// read naturally in every format without a math engine.
enum MarkdownMath {
    static func unicode(_ tex: String) -> String {
        var result = ""
        let characters = Array(tex)
        var index = 0
        while index < characters.count {
            let character = characters[index]
            switch character {
            case "\\":
                var name = ""
                var next = index + 1
                while next < characters.count, characters[next].isLetter {
                    name.append(characters[next])
                    next += 1
                }
                if name.isEmpty, next < characters.count {
                    // \, \; \! \{ and friends.
                    let symbol = characters[next]
                    result += [",": "\u{2009}", ";": " ", ":": " ", "!": "", " ": " "][symbol] ?? String(symbol)
                    index = next + 1
                    continue
                }
                if name == "frac" || name == "dfrac" || name == "tfrac",
                   let (numerator, afterNumerator) = group(characters, from: next),
                   let (denominator, afterDenominator) = group(characters, from: afterNumerator) {
                    let top = unicode(numerator)
                    let bottom = unicode(denominator)
                    result += "\(parenthesized(top))/\(parenthesized(bottom))"
                    index = afterDenominator
                    continue
                }
                if name == "sqrt", let (radicand, after) = group(characters, from: next) {
                    result += "√" + parenthesized(unicode(radicand))
                    index = after
                    continue
                }
                if ["text", "mathrm", "mathit", "mathbf", "operatorname", "textrm", "mbox"].contains(name),
                   let (content, after) = group(characters, from: next) {
                    result += name.hasPrefix("text") || name == "mbox" ? content : unicode(content)
                    index = after
                    continue
                }
                result += symbols[name] ?? (["left", "right", "big", "Big", "displaystyle", "limits"].contains(name) ? "" : name)
                index = next
            case "^", "_":
                let map = character == "^" ? superscripts : subscripts
                if let (content, after) = group(characters, from: index + 1) {
                    let converted = unicode(content)
                    if let scripted = mapped(converted, map) {
                        result += scripted
                    } else {
                        result += String(character) + parenthesized(converted)
                    }
                    index = after
                } else if index + 1 < characters.count {
                    let single = String(characters[index + 1])
                    result += mapped(single, map) ?? String(character) + single
                    index += 2
                } else {
                    index += 1
                }
            case "{", "}":
                index += 1
            default:
                result.append(character)
                index += 1
            }
        }
        return result.replacingOccurrences(of: "  ", with: " ")
    }

    /// The contents of `{…}` at `start` (or the single character there) and
    /// the index after it.
    private static func group(_ characters: [Character], from start: Int) -> (String, Int)? {
        var index = start
        while index < characters.count, characters[index] == " " { index += 1 }
        guard index < characters.count else { return nil }
        guard characters[index] == "{" else {
            if characters[index] == "\\" {
                var end = index + 1
                while end < characters.count, characters[end].isLetter { end += 1 }
                return (String(characters[index..<max(end, index + 2)]), max(end, index + 2))
            }
            return (String(characters[index]), index + 1)
        }
        var depth = 0
        var end = index
        while end < characters.count {
            if characters[end] == "{" { depth += 1 }
            if characters[end] == "}" {
                depth -= 1
                if depth == 0 { return (String(characters[(index + 1)..<end]), end + 1) }
            }
            end += 1
        }
        return nil
    }

    private static func parenthesized(_ text: String) -> String {
        text.count <= 1 || text.allSatisfy({ $0.isLetter || $0.isNumber }) ? text : "(\(text))"
    }

    private static func mapped(_ text: String, _ map: [Character: Character]) -> String? {
        var result = ""
        for character in text {
            guard let scripted = map[character] else { return nil }
            result.append(scripted)
        }
        return result
    }

    private static let superscripts: [Character: Character] = [
        "0": "⁰", "1": "¹", "2": "²", "3": "³", "4": "⁴", "5": "⁵", "6": "⁶", "7": "⁷", "8": "⁸", "9": "⁹",
        "+": "⁺", "-": "⁻", "=": "⁼", "(": "⁽", ")": "⁾", "n": "ⁿ", "i": "ⁱ", "x": "ˣ", "T": "ᵀ", "′": "′",
    ]

    private static let subscripts: [Character: Character] = [
        "0": "₀", "1": "₁", "2": "₂", "3": "₃", "4": "₄", "5": "₅", "6": "₆", "7": "₇", "8": "₈", "9": "₉",
        "+": "₊", "-": "₋", "=": "₌", "(": "₍", ")": "₎", "a": "ₐ", "e": "ₑ", "i": "ᵢ", "j": "ⱼ", "k": "ₖ",
        "n": "ₙ", "o": "ₒ", "x": "ₓ", "t": "ₜ",
    ]

    private static let symbols: [String: String] = [
        "alpha": "α", "beta": "β", "gamma": "γ", "delta": "δ", "epsilon": "ε", "varepsilon": "ε", "zeta": "ζ",
        "eta": "η", "theta": "θ", "vartheta": "ϑ", "iota": "ι", "kappa": "κ", "lambda": "λ", "mu": "μ", "nu": "ν",
        "xi": "ξ", "pi": "π", "varpi": "ϖ", "rho": "ρ", "varrho": "ϱ", "sigma": "σ", "varsigma": "ς", "tau": "τ",
        "upsilon": "υ", "phi": "φ", "varphi": "φ", "chi": "χ", "psi": "ψ", "omega": "ω",
        "Gamma": "Γ", "Delta": "Δ", "Theta": "Θ", "Lambda": "Λ", "Xi": "Ξ", "Pi": "Π", "Sigma": "Σ",
        "Upsilon": "Υ", "Phi": "Φ", "Psi": "Ψ", "Omega": "Ω",
        "int": "∫", "iint": "∬", "oint": "∮", "sum": "∑", "prod": "∏", "infty": "∞", "partial": "∂", "nabla": "∇",
        "pm": "±", "mp": "∓", "times": "×", "div": "÷", "cdot": "·", "cdots": "⋯", "ldots": "…", "dots": "…",
        "leq": "≤", "le": "≤", "geq": "≥", "ge": "≥", "neq": "≠", "ne": "≠", "approx": "≈", "equiv": "≡",
        "sim": "∼", "simeq": "≃", "propto": "∝", "ll": "≪", "gg": "≫",
        "to": "→", "rightarrow": "→", "leftarrow": "←", "Rightarrow": "⇒", "Leftarrow": "⇐",
        "leftrightarrow": "↔", "Leftrightarrow": "⇔", "mapsto": "↦", "implies": "⇒", "iff": "⇔",
        "in": "∈", "notin": "∉", "subset": "⊂", "subseteq": "⊆", "supset": "⊃", "supseteq": "⊇",
        "cup": "∪", "cap": "∩", "emptyset": "∅", "forall": "∀", "exists": "∃", "neg": "¬", "land": "∧", "lor": "∨",
        "angle": "∠", "perp": "⊥", "parallel": "∥", "circ": "∘", "degree": "°", "prime": "′",
        "langle": "⟨", "rangle": "⟩", "lfloor": "⌊", "rfloor": "⌋", "lceil": "⌈", "rceil": "⌉",
        "quad": " ", "qquad": "  ", "hbar": "ℏ", "ell": "ℓ", "Re": "ℜ", "Im": "ℑ", "aleph": "ℵ",
        "sin": "sin", "cos": "cos", "tan": "tan", "log": "log", "ln": "ln", "exp": "exp", "lim": "lim",
        "max": "max", "min": "min", "det": "det",
    ]
}
