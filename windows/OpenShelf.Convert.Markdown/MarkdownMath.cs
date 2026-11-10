using System.Text;

namespace OpenShelf.Convert.Markdown;

/// <summary>Shows TeX math as readable Unicode text: <c>\omega = d\phi / dt</c> becomes "ω = dφ / dt" and <c>R^{2}</c> becomes
/// "R²". It is no typesetter, but formulas read naturally in every format without a math engine. Port of MarkdownMath.swift.</summary>
public static class MarkdownMath
{
    public static string Unicode(string tex)
    {
        var result = new StringBuilder();
        var characters = tex.ToCharArray();
        var index = 0;
        while (index < characters.Length)
        {
            var character = characters[index];
            switch (character)
            {
                case '\\':
                {
                    var name = new StringBuilder();
                    var next = index + 1;
                    while (next < characters.Length && char.IsLetter(characters[next]))
                    {
                        name.Append(characters[next]);
                        next++;
                    }
                    var word = name.ToString();
                    if (word.Length == 0 && next < characters.Length)
                    {
                        // \, \; \! \{ and friends.
                        var symbol = characters[next];
                        result.Append(symbol switch { ',' => " ", ';' => " ", ':' => " ", '!' => "", ' ' => " ", _ => symbol.ToString() });
                        index = next + 1;
                        continue;
                    }
                    if (word is "frac" or "dfrac" or "tfrac" && Group(characters, next) is { } numerator
                        && Group(characters, numerator.After) is { } denominator)
                    {
                        result.Append($"{Parenthesized(Unicode(numerator.Text))}/{Parenthesized(Unicode(denominator.Text))}");
                        index = denominator.After;
                        continue;
                    }
                    if (word == "sqrt" && Group(characters, next) is { } radicand)
                    {
                        result.Append('√').Append(Parenthesized(Unicode(radicand.Text)));
                        index = radicand.After;
                        continue;
                    }
                    if (word is "text" or "mathrm" or "mathit" or "mathbf" or "operatorname" or "textrm" or "mbox" && Group(characters, next) is { } content)
                    {
                        result.Append(word.StartsWith("text", StringComparison.Ordinal) || word == "mbox" ? content.Text : Unicode(content.Text));
                        index = content.After;
                        continue;
                    }
                    result.Append(Symbols.TryGetValue(word, out var known) ? known
                        : word is "left" or "right" or "big" or "Big" or "displaystyle" or "limits" ? "" : word);
                    index = next;
                    break;
                }
                case '^' or '_':
                {
                    var map = character == '^' ? Superscripts : Subscripts;
                    if (Group(characters, index + 1) is { } group)
                    {
                        var converted = Unicode(group.Text);
                        result.Append(Mapped(converted, map) ?? character + Parenthesized(converted));
                        index = group.After;
                    }
                    else if (index + 1 < characters.Length)
                    {
                        var single = characters[index + 1].ToString();
                        result.Append(Mapped(single, map) ?? character + single);
                        index += 2;
                    }
                    else index++;
                    break;
                }
                case '{' or '}':
                    index++;
                    break;
                default:
                    result.Append(character);
                    index++;
                    break;
            }
        }
        return result.ToString().Replace("  ", " ");
    }

    /// <summary>The contents of <c>{...}</c> at <paramref name="start"/> (or the single character there) and the index after it.</summary>
    private static (string Text, int After)? Group(char[] characters, int start)
    {
        var index = start;
        while (index < characters.Length && characters[index] == ' ') index++;
        if (index >= characters.Length) return null;
        if (characters[index] != '{')
        {
            if (characters[index] == '\\')
            {
                var stop = index + 1;
                while (stop < characters.Length && char.IsLetter(characters[stop])) stop++;
                var limit = Math.Min(Math.Max(stop, index + 2), characters.Length);
                return (new string(characters, index, limit - index), Math.Max(stop, index + 2));
            }
            return (characters[index].ToString(), index + 1);
        }
        var depth = 0;
        var end = index;
        while (end < characters.Length)
        {
            if (characters[end] == '{') depth++;
            if (characters[end] == '}')
            {
                depth--;
                if (depth == 0) return (new string(characters, index + 1, end - index - 1), end + 1);
            }
            end++;
        }
        return null;
    }

    private static string Parenthesized(string text) =>
        text.Length <= 1 || text.All(c => char.IsLetter(c) || char.IsNumber(c)) ? text : $"({text})";

    private static string? Mapped(string text, Dictionary<char, char> map)
    {
        var result = new StringBuilder();
        foreach (var character in text)
        {
            if (!map.TryGetValue(character, out var scripted)) return null;
            result.Append(scripted);
        }
        return result.ToString();
    }

    private static readonly Dictionary<char, char> Superscripts = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴', ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾', ['n'] = 'ⁿ', ['i'] = 'ⁱ', ['x'] = 'ˣ', ['T'] = 'ᵀ', ['′'] = '′',
    };

    private static readonly Dictionary<char, char> Subscripts = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄', ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎', ['a'] = 'ₐ', ['e'] = 'ₑ', ['i'] = 'ᵢ', ['j'] = 'ⱼ', ['k'] = 'ₖ',
        ['n'] = 'ₙ', ['o'] = 'ₒ', ['x'] = 'ₓ', ['t'] = 'ₜ',
    };

    private static readonly Dictionary<string, string> Symbols = new()
    {
        ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ", ["epsilon"] = "ε", ["varepsilon"] = "ε", ["zeta"] = "ζ",
        ["eta"] = "η", ["theta"] = "θ", ["vartheta"] = "ϑ", ["iota"] = "ι", ["kappa"] = "κ", ["lambda"] = "λ", ["mu"] = "μ", ["nu"] = "ν",
        ["xi"] = "ξ", ["pi"] = "π", ["varpi"] = "ϖ", ["rho"] = "ρ", ["varrho"] = "ϱ", ["sigma"] = "σ", ["varsigma"] = "ς", ["tau"] = "τ",
        ["upsilon"] = "υ", ["phi"] = "φ", ["varphi"] = "φ", ["chi"] = "χ", ["psi"] = "ψ", ["omega"] = "ω",
        ["Gamma"] = "Γ", ["Delta"] = "Δ", ["Theta"] = "Θ", ["Lambda"] = "Λ", ["Xi"] = "Ξ", ["Pi"] = "Π", ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ", ["Phi"] = "Φ", ["Psi"] = "Ψ", ["Omega"] = "Ω",
        ["int"] = "∫", ["iint"] = "∬", ["oint"] = "∮", ["sum"] = "∑", ["prod"] = "∏", ["infty"] = "∞", ["partial"] = "∂", ["nabla"] = "∇",
        ["pm"] = "±", ["mp"] = "∓", ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["cdots"] = "⋯", ["ldots"] = "…", ["dots"] = "…",
        ["leq"] = "≤", ["le"] = "≤", ["geq"] = "≥", ["ge"] = "≥", ["neq"] = "≠", ["ne"] = "≠", ["approx"] = "≈", ["equiv"] = "≡",
        ["sim"] = "∼", ["simeq"] = "≃", ["propto"] = "∝", ["ll"] = "≪", ["gg"] = "≫",
        ["to"] = "→", ["rightarrow"] = "→", ["leftarrow"] = "←", ["Rightarrow"] = "⇒", ["Leftarrow"] = "⇐",
        ["leftrightarrow"] = "↔", ["Leftrightarrow"] = "⇔", ["mapsto"] = "↦", ["implies"] = "⇒", ["iff"] = "⇔",
        ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["subseteq"] = "⊆", ["supset"] = "⊃", ["supseteq"] = "⊇",
        ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅", ["forall"] = "∀", ["exists"] = "∃", ["neg"] = "¬", ["land"] = "∧", ["lor"] = "∨",
        ["angle"] = "∠", ["perp"] = "⊥", ["parallel"] = "∥", ["circ"] = "∘", ["degree"] = "°", ["prime"] = "′",
        ["langle"] = "⟨", ["rangle"] = "⟩", ["lfloor"] = "⌊", ["rfloor"] = "⌋", ["lceil"] = "⌈", ["rceil"] = "⌉",
        ["quad"] = " ", ["qquad"] = "  ", ["hbar"] = "ℏ", ["ell"] = "ℓ", ["Re"] = "ℜ", ["Im"] = "ℑ", ["aleph"] = "ℵ",
        ["sin"] = "sin", ["cos"] = "cos", ["tan"] = "tan", ["log"] = "log", ["ln"] = "ln", ["exp"] = "exp", ["lim"] = "lim",
        ["max"] = "max", ["min"] = "min", ["det"] = "det",
    };
}
