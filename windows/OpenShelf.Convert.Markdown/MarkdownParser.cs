using System.Text;
using System.Text.RegularExpressions;

namespace OpenShelf.Convert.Markdown;

/// <summary>Small string helpers with the semantics of the Swift ones (<c>isBlank</c>, <c>indentation</c>, <c>.whitespaces</c>).</summary>
internal static class MdText
{
    public static bool IsBlank(this string s)
    {
        foreach (var c in s) if (c != ' ' && c != '\t') return false;
        return true;
    }

    public static int Indentation(this string s)
    {
        var n = 0;
        while (n < s.Length && s[n] == ' ') n++;
        return n;
    }

    /// <summary>Removes up to <paramref name="count"/> leading spaces.</summary>
    public static string DropLeadingSpaces(this string s, int count) => s[Math.Min(count, s.Indentation())..];

    private static bool IsHorizontalSpace(char c) => c == ' ' || c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;

    /// <summary>Swift's <c>trimmingCharacters(in: .whitespaces)</c>: spaces and tabs, not line breaks.</summary>
    public static string TrimSpaces(this string s)
    {
        var start = 0;
        var end = s.Length;
        while (start < end && IsHorizontalSpace(s[start])) start++;
        while (end > start && IsHorizontalSpace(s[end - 1])) end--;
        return s[start..end];
    }

    /// <summary>Swift's <c>.whitespacesAndNewlines</c>.</summary>
    public static string TrimAll(this string s) => s.Trim();

    public static string DropWhileSpace(this string s)
    {
        var i = 0;
        while (i < s.Length && s[i] == ' ') i++;
        return s[i..];
    }

    public static string TrimEndSpaces(this string s) => s.TrimEnd(' ');

    public static bool IsPunctuationOrSymbol(char c) => char.IsPunctuation(c) || char.IsSymbol(c);

    public static string Join(this IEnumerable<string> parts, string separator) => string.Join(separator, parts);
}

/// <summary>Finds the blocks of a Markdown document, then the inline content of each. Port of MarkdownParser.</summary>
public sealed class MarkdownParser
{
    private readonly Dictionary<string, (string Url, string? Title)> _references = [];
    private readonly Dictionary<string, List<MarkdownBlock<string>>> _footnoteSources = [];
    private readonly Dictionary<string, int> _footnoteNumbers = [];
    private readonly List<List<MarkdownBlock<MarkdownText>>> _footnotes = [];
    private readonly HashSet<string> _headingIds = [];

    public static MarkdownDocument Parse(string source)
    {
        var parser = new MarkdownParser();
        var lines = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').Select(ExpandingLeadingTabs).ToList();
        var title = TakeTitle(lines);
        var raw = parser.Blocks(lines);
        var blocks = new List<MarkdownBlock<MarkdownText>>();
        foreach (var block in raw) blocks.Add(parser.Resolve(block));
        return new MarkdownDocument(title, blocks, parser._footnotes);
    }

    // MARK: Front matter

    private static readonly Regex YamlKey = new(@"^[A-Za-z_][\w-]*:");
    private static readonly Regex YamlTitle = new(@"^title:\s*(.*?)\s*$");

    /// <summary>Removes a YAML block (<c>---</c> ... <c>---</c>) or a Pandoc title block (<c>% Title</c>) from the top,
    /// returning its title.</summary>
    public static string? TakeTitle(List<string> lines)
    {
        if (lines.Count > 0 && lines[0].StartsWith("% ", StringComparison.Ordinal))
        {
            var title = lines[0][2..].TrimSpaces();
            lines.RemoveAt(0);
            while (lines.Count > 0 && lines[0].StartsWith("% ", StringComparison.Ordinal)) lines.RemoveAt(0);
            return title.Length == 0 ? null : title;
        }
        if (lines.Count == 0 || lines[0].TrimSpaces() != "---") return null;
        var end = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            var t = lines[i].TrimSpaces();
            if (t == "---" || t == "...") { end = i; break; }
        }
        if (end < 0) return null;
        var hasKey = false;
        for (var i = 1; i < end; i++) if (YamlKey.IsMatch(lines[i])) { hasKey = true; break; }
        if (!hasKey) return null;
        string? found = null;
        for (var i = 1; i < end && found is null; i++)
        {
            var match = YamlTitle.Match(lines[i]);
            if (!match.Success) continue;
            var value = match.Groups[1].Value;
            if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\'')) value = value[1..^1];
            found = value;
        }
        lines.RemoveRange(0, end + 1);
        return string.IsNullOrEmpty(found) ? null : found;
    }

    // MARK: Finding blocks

    private List<MarkdownBlock<string>> Blocks(List<string> lines)
    {
        var blocks = new List<MarkdownBlock<string>>();
        var index = 0;
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.IsBlank()) { index++; continue; }
            var indent = line.Indentation();
            if (indent >= 4) { index = IndentedCode(lines, index, blocks); continue; }
            var trimmed = line[indent..];
            int? next = FencedCode(lines, index, blocks) ?? DisplayMath(lines, index, blocks) ?? MultilineTable(lines, index, blocks)
                ?? PipeTable(lines, index, blocks) ?? SimpleTable(lines, index, blocks);
            if (next is { } jump) { index = jump; continue; }
            var heading = AtxHeading(trimmed);
            if (heading is not null)
            {
                blocks.Add(new MarkdownBlock<string>.Heading(heading.Value.Level, heading.Value.Text, heading.Value.Id ?? ""));
                index++;
            }
            else if (IsRule(trimmed)) { blocks.Add(new MarkdownBlock<string>.Rule()); index++; }
            else if (trimmed.StartsWith('>')) index = Quote(lines, index, blocks);
            else if (ListMarker(line) is not null) index = ParseList(lines, index, blocks);
            else if ((FootnoteDefinition(lines, index) ?? ReferenceDefinition(lines, index)) is { } after) index = after;
            else if (trimmed == "|" || trimmed.StartsWith("| ", StringComparison.Ordinal)) index = LineBlock(lines, index, blocks);
            else if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                var close = -1;
                for (var i = index; i < lines.Count; i++) if (lines[i].Contains("-->", StringComparison.Ordinal)) { close = i; break; }
                index = close < 0 ? lines.Count : close + 1;
            }
            else if (DefinitionList(lines, index, blocks) is { } defEnd) index = defEnd;
            else index = Paragraph(lines, index, blocks);
        }
        return AttachingCaptions(blocks);
    }

    private static int IndentedCode(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var end = start;
        while (end < lines.Count && (lines[end].IsBlank() || lines[end].Indentation() >= 4)) end++;
        while (end > start && lines[end - 1].IsBlank()) end--;
        var code = lines.GetRange(start, end - start).Select(l => l.DropLeadingSpaces(4)).Join("\n");
        blocks.Add(new MarkdownBlock<string>.Code(code, null));
        return end;
    }

    private static readonly Regex FenceStart = new(@"^(`{3,}|~{3,})[ \t]*(.*)$");

    private static int? FencedCode(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var line = lines[start];
        var indent = line.Indentation();
        if (indent >= 4) return null;
        var match = FenceStart.Match(line[indent..]);
        if (!match.Success) return null;
        var fence = match.Groups[1].Value;
        var info = match.Groups[2].Value.TrimSpaces();
        if (fence[0] == '`' && info.Contains('`')) return null;
        var end = start + 1;
        var code = new List<string>();
        while (end < lines.Count)
        {
            var candidate = lines[end].TrimSpaces();
            if (lines[end].Indentation() < 4 && candidate.Length >= fence.Length && candidate.All(c => c == fence[0]))
            {
                end++;
                break;
            }
            code.Add(lines[end].DropLeadingSpaces(indent));
            end++;
        }
        blocks.Add(new MarkdownBlock<string>.Code(code.Join("\n"), LanguageFromInfo(info)));
        return end;
    }

    /// <summary>"python", "{.python}", "{.python .numberLines}" and "python title=x" all mean Python.</summary>
    public static string? LanguageFromInfo(string info)
    {
        var word = info.Trim('{', '}', ' ').Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        var language = word.StartsWith('.') ? word[1..] : word;
        return language.Length == 0 || language.Contains('=') || language.StartsWith('#') ? null : language;
    }

    private static int? DisplayMath(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var first = lines[start].TrimSpaces();
        if (!first.StartsWith("$$", StringComparison.Ordinal)) return null;
        var rest = first[2..];
        var close = rest.IndexOf("$$", StringComparison.Ordinal);
        if (close >= 0)
        {
            // `$$x$$` on one line; text after it would be a paragraph, which inline math handles.
            if (rest[(close + 2)..].TrimSpaces().Length != 0) return null;
            blocks.Add(new MarkdownBlock<string>.Math(rest[..close].TrimSpaces()));
            return start + 1;
        }
        var tex = new List<string> { rest };
        var end = start + 1;
        while (end < lines.Count)
        {
            var at = lines[end].IndexOf("$$", StringComparison.Ordinal);
            if (at >= 0)
            {
                tex.Add(lines[end][..at]);
                blocks.Add(new MarkdownBlock<string>.Math(tex.Join("\n").TrimAll()));
                return end + 1;
            }
            if (lines[end].IsBlank()) return null;
            tex.Add(lines[end]);
            end++;
        }
        return null;
    }

    private static readonly Regex AtxPattern = new(@"^(#{1,6})(?:[ \t]+(.*?))?[ \t]*$");
    private static readonly Regex ClosingHashes = new(@"(?:^|[ \t]+)#+$");
    private static readonly Regex HeadingAttributes = new(@"[ \t]*\{([^}]*)\}$");

    public static (int Level, string Text, string? Id)? AtxHeading(string line)
    {
        var match = AtxPattern.Match(line);
        if (!match.Success) return null;
        var text = match.Groups[2].Success ? match.Groups[2].Value : "";
        // Closing hashes, when separated by a space: "## Title ##".
        var closing = ClosingHashes.Match(text);
        if (closing.Success) text = text.Remove(closing.Index, closing.Length);
        string? id = null;
        var attributes = HeadingAttributes.Match(text);
        if (attributes.Success)
        {
            var word = attributes.Groups[1].Value.Split(' ').FirstOrDefault(w => w.StartsWith('#'));
            id = word?[1..];
            text = text.Remove(attributes.Index, attributes.Length);
        }
        return (match.Groups[1].Length, text.TrimSpaces(), id);
    }

    private static readonly Regex RulePattern = new(@"^([-*_])(?:[ \t]*\1){2,}[ \t]*$");

    public static bool IsRule(string line) => RulePattern.IsMatch(line);

    private int Quote(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var content = new List<string>();
        var end = start;
        while (end < lines.Count)
        {
            var line = lines[end];
            var trimmed = line.DropWhileSpace();
            if (line.Indentation() < 4 && trimmed.StartsWith('>'))
            {
                var rest = trimmed[1..];
                if (rest.StartsWith(' ')) rest = rest[1..];
                content.Add(rest);
            }
            else if (!line.IsBlank() && content.Count > 0 && !content[^1].IsBlank() && !StartsBlock(line))
            {
                // A lazy continuation of the quoted paragraph.
                content.Add(line);
            }
            else break;
            end++;
        }
        blocks.Add(new MarkdownBlock<string>.Quote(Blocks(content)));
        return end;
    }

    // MARK: Lists

    public sealed record ListMarkerInfo(int Indent, bool Ordered, char Symbol, int Number, int ContentIndent, string Content);

    private static readonly Regex BulletMarker = new(@"^([-*+])(?=[ \t]|$)");
    private static readonly Regex NumberMarker = new(@"^([0-9]{1,9})([.)])(?=[ \t]|$)");

    public static ListMarkerInfo? ListMarker(string line)
    {
        var indent = line.Indentation();
        if (indent >= 4) return null;
        var rest = line[indent..];
        bool ordered;
        char symbol;
        int number;
        int width;
        var bullet = BulletMarker.Match(rest);
        if (bullet.Success) { ordered = false; symbol = bullet.Groups[1].Value[0]; number = 1; width = 1; }
        else
        {
            var numbered = NumberMarker.Match(rest);
            if (!numbered.Success) return null;
            ordered = true;
            symbol = numbered.Groups[2].Value[0];
            number = int.TryParse(numbered.Groups[1].Value, out var n) ? n : 1;
            width = numbered.Length;
        }
        var afterMarker = rest[width..];
        var spaces = afterMarker.Indentation();
        var content = afterMarker[spaces..];
        // Five or more spaces start indented code inside the item, one space in.
        var gap = content.Length == 0 || spaces > 4 ? 1 : spaces;
        return new ListMarkerInfo(indent, ordered, symbol, number, indent + width + gap,
            spaces > 4 ? new string(' ', spaces - 1) + content : content);
    }

    private int ParseList(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var first = ListMarker(lines[start]);
        if (first is null) return start + 1;
        var items = new List<List<string>>();
        var current = new List<string> { TaskBox(first.Content) };
        var contentIndent = first.ContentIndent;
        var loose = false;
        var end = start + 1;
        while (end < lines.Count)
        {
            var line = lines[end];
            if (line.IsBlank())
            {
                current.Add("");
                end++;
                continue;
            }
            var previousWasBlank = current.Count > 0 && current[^1].IsBlank();
            ListMarkerInfo? marker;
            if (line.Indentation() >= contentIndent)
            {
                if (previousWasBlank && current.Any(l => !l.IsBlank()) && current.Count >= 2 && !current[^2].IsBlank()) loose = true;
                current.Add(line.DropLeadingSpaces(contentIndent));
            }
            else if ((marker = ListMarker(line)) is not null && marker.Ordered == first.Ordered && (marker.Symbol == first.Symbol || first.Ordered))
            {
                if (previousWasBlank) loose = true;
                items.Add(current);
                current = [TaskBox(marker.Content)];
                contentIndent = marker.ContentIndent;
            }
            else if (!previousWasBlank && !StartsBlock(line))
            {
                // Lazy continuation of the item's paragraph.
                current.Add(line.TrimSpaces());
            }
            else break;
            end++;
        }
        items.Add(current);
        // Blank lines after the last item belong to no one.
        while (end > start && lines[end - 1].IsBlank()) end--;
        var parsedItems = new List<List<MarkdownBlock<string>>>();
        foreach (var item in items)
        {
            var count = item.Count;
            while (count > 0 && item[count - 1].IsBlank()) count--;
            var trimmedItem = item.GetRange(0, count);
            // A blank line between two of the item's own blocks makes the list loose.
            if (trimmedItem.Skip(1).Any(l => l.IsBlank()))
            {
                var lastBlank = trimmedItem.FindLastIndex(l => l.IsBlank());
                if (lastBlank >= 0 && trimmedItem.Skip(lastBlank).Any(l => !l.IsBlank() && l.Indentation() < 4 && ListMarker(l) is null)) loose = true;
            }
            parsedItems.Add(Blocks(trimmedItem));
        }
        blocks.Add(new MarkdownBlock<string>.ListBlock(new MarkdownList<string> { Ordered = first.Ordered, Start = first.Number, Tight = !loose, Items = parsedItems }));
        return end;
    }

    private static readonly Regex TaskPattern = new(@"^\[([ xX])\][ \t]+");

    /// <summary>"[ ] Buy milk" and "[x] Done" become ballot boxes.</summary>
    public static string TaskBox(string content)
    {
        var match = TaskPattern.Match(content);
        if (!match.Success) return content;
        return (match.Groups[1].Value == " " ? "\u2610 " : "\u2611 ") + content[match.Length..];
    }

    // MARK: Definitions

    private static readonly Regex FootnoteDef = new(@"^ {0,3}\[\^([^\]\s]+)\]:[ \t]?(.*)$");

    private int? FootnoteDefinition(List<string> lines, int start)
    {
        var match = FootnoteDef.Match(lines[start]);
        if (!match.Success) return null;
        var label = match.Groups[1].Value.ToLowerInvariant();
        var content = new List<string> { match.Groups[2].Value };
        var end = start + 1;
        while (end < lines.Count)
        {
            var line = lines[end];
            if (line.IsBlank()) content.Add("");
            else if (line.Indentation() >= 4) content.Add(line.DropLeadingSpaces(4));
            else if (!content[^1].IsBlank() && !StartsBlock(line) && !line.StartsWith("[^", StringComparison.Ordinal)) content.Add(line);
            else break;
            end++;
        }
        while (end > start + 1 && lines[end - 1].IsBlank()) end--;
        if (!_footnoteSources.ContainsKey(label)) _footnoteSources[label] = Blocks(content);
        return end;
    }

    private static readonly Regex ReferenceDef = new(
        "^ {0,3}\\[([^\\]^][^\\]]*)\\]:[ \\t]*<?([^\\s>]+)>?(?:[ \\t]+(?:\"([^\"]*)\"|'([^']*)'|\\(([^)]*)\\)))?[ \\t]*$");

    private int? ReferenceDefinition(List<string> lines, int start)
    {
        var match = ReferenceDef.Match(lines[start]);
        if (!match.Success) return null;
        var label = NormalizedLabel(match.Groups[1].Value);
        if (!_references.ContainsKey(label))
        {
            string? title = null;
            for (var g = 3; g <= 5; g++) if (match.Groups[g].Success) { title = match.Groups[g].Value; break; }
            _references[label] = (match.Groups[2].Value, title);
        }
        return start + 1;
    }

    public static string NormalizedLabel(string label) =>
        label.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Join(" ");

    private static int LineBlock(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var result = new List<string>();
        var end = start;
        while (end < lines.Count)
        {
            var line = lines[end];
            var trimmed = line.DropWhileSpace();
            if (trimmed == "|" || trimmed.StartsWith("| ", StringComparison.Ordinal))
            {
                var text = trimmed[(trimmed.Length > 1 ? 2 : 1)..];
                // Leading spaces are kept as no-break spaces, which every renderer keeps.
                var leading = text.Indentation();
                result.Add(new string('\u00A0', leading) + text[leading..]);
            }
            else if (!line.IsBlank() && line.StartsWith(' ') && result.Count > 0)
            {
                result[^1] += " " + line.TrimSpaces();
            }
            else break;
            end++;
        }
        blocks.Add(new MarkdownBlock<string>.Lines(result));
        return end;
    }

    private static readonly Regex DefinitionMarkerPattern = new(@"^( {0,3})[:~]([ \t]+)(.*)$");

    /// <summary><c>Term</c> followed by <c>: definition</c> (or <c>~ definition</c>), optionally after one blank line.</summary>
    private int? DefinitionList(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        (string Content, int Indent)? DefinitionMarker(string line)
        {
            var match = DefinitionMarkerPattern.Match(line);
            return match.Success ? (match.Groups[3].Value, match.Groups[1].Length + 1 + match.Groups[2].Length) : null;
        }
        int? DefinitionStarts(int term)
        {
            if (term + 1 < lines.Count && DefinitionMarker(lines[term + 1]) is not null) return term + 1;
            if (term + 2 < lines.Count && lines[term + 1].IsBlank() && DefinitionMarker(lines[term + 2]) is not null) return term + 2;
            return null;
        }
        if (DefinitionStarts(start) is null) return null;

        var items = new List<MarkdownDefinition<string>>();
        var index = start;
        while (index < lines.Count && !lines[index].IsBlank() && DefinitionStarts(index) is { } first)
        {
            var term = lines[index].TrimSpaces();
            var definitions = new List<List<MarkdownBlock<string>>>();
            var position = first;
            while (position < lines.Count && DefinitionMarker(lines[position]) is { } marker)
            {
                var content = new List<string> { marker.Content };
                var end = position + 1;
                while (end < lines.Count)
                {
                    var line = lines[end];
                    if (line.IsBlank())
                    {
                        // Continues only when indented text or another definition follows.
                        if (!(end + 1 < lines.Count && (lines[end + 1].Indentation() >= 2 || DefinitionMarker(lines[end + 1]) is not null))) break;
                        if (DefinitionMarker(lines[end + 1]) is not null) break;
                        content.Add("");
                    }
                    else if (DefinitionMarker(line) is not null) break;
                    else if (line.Indentation() >= 2) content.Add(line.DropLeadingSpaces(Math.Min(marker.Indent, 4)));
                    else if (!content[^1].IsBlank() && !StartsBlock(line) && DefinitionStarts(end) is null)
                    {
                        // Lazy continuation, unless the line is the next term.
                        content.Add(line);
                    }
                    else break;
                    end++;
                }
                definitions.Add(Blocks(content));
                position = end;
                if (position + 1 < lines.Count && lines[position].IsBlank() && DefinitionMarker(lines[position + 1]) is not null) position++;
            }
            items.Add(new MarkdownDefinition<string> { Term = term, Definitions = definitions });
            index = position;
            // Terms may be separated by one blank line.
            if (index + 1 < lines.Count && lines[index].IsBlank() && !lines[index + 1].IsBlank() && DefinitionStarts(index + 1) is not null) index++;
        }
        blocks.Add(new MarkdownBlock<string>.Definitions(items));
        return index;
    }

    // MARK: Paragraphs

    private static readonly Regex SetextPattern = new(@"^ {0,3}(=+|-+)[ \t]*$");

    private static int Paragraph(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var text = new List<string> { lines[start].TrimSpaces() };
        var end = start + 1;
        while (end < lines.Count)
        {
            var line = lines[end];
            if (line.IsBlank()) break;
            Match match;
            if (line.Indentation() < 4 && (match = SetextPattern.Match(line)).Success)
            {
                var raw = text.Join("\n");
                var heading = AtxHeading("# " + raw);
                blocks.Add(new MarkdownBlock<string>.Heading(match.Groups[1].Value[0] == '=' ? 1 : 2, heading?.Text ?? raw, heading?.Id ?? ""));
                return end + 1;
            }
            if (StartsBlock(line)) break;
            // Keep the two trailing spaces of a hard line break.
            text.Add(line.EndsWith("  ", StringComparison.Ordinal) ? line.DropWhileSpace() : line.TrimSpaces());
            end++;
        }
        // A trailing hard break means nothing at the end of a paragraph.
        if (text[^1].EndsWith(' ')) text[^1] = text[^1].TrimSpaces();
        blocks.Add(new MarkdownBlock<string>.Paragraph(text.Join("\n")));
        return end;
    }

    /// <summary>Lines that end a paragraph without a blank line before them.</summary>
    public static bool StartsBlock(string line)
    {
        if (line.Indentation() >= 4) return false;
        var trimmed = line.DropWhileSpace();
        if (trimmed.StartsWith('>') || trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal)
            || trimmed.StartsWith("$$", StringComparison.Ordinal)) return true;
        if (AtxHeading(trimmed) is not null || IsRule(trimmed)) return true;
        if (ListMarker(line) is { Content.Length: > 0 } marker) return !marker.Ordered || marker.Number == 1;
        return false;
    }

    // MARK: Tables

    private static readonly Regex PipeDelimiter = new(@"^[ \t]*\|?[ \t]*:?-+:?[ \t]*(\|[ \t]*:?-+:?[ \t]*)*\|?[ \t]*$");

    /// <summary>GitHub's pipe tables.</summary>
    private static int? PipeTable(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        if (!(start + 1 < lines.Count && lines[start].Contains('|'))) return null;
        var delimiter = lines[start + 1];
        if (!(delimiter.Contains('|') || lines[start].TrimSpaces().StartsWith('|')) || !PipeDelimiter.IsMatch(delimiter)) return null;
        var alignments = PipeCells(delimiter).Select(cell => (cell.StartsWith(':'), cell.EndsWith(':')) switch
        {
            (true, true) => MarkdownAlignment.Center,
            (true, false) => MarkdownAlignment.Left,
            (false, true) => MarkdownAlignment.Right,
            _ => MarkdownAlignment.None,
        }).ToList();
        var header = PipeCells(lines[start]);
        if (header.Count != alignments.Count) return null;
        var rows = new List<List<string>>();
        var end = start + 2;
        while (end < lines.Count && !lines[end].IsBlank() && (lines[end].Contains('|') || !StartsBlock(lines[end])))
        {
            if (!lines[end].Contains('|')) break;
            rows.Add(Fitted(PipeCells(lines[end]), alignments.Count));
            end++;
        }
        var table = new MarkdownTable<string> { Header = header, Rows = rows, Alignments = alignments };
        return Caption(table, lines, end, blocks);
    }

    /// <summary>Splits a pipe-table row at <c>|</c>, except escaped ones and those inside code.</summary>
    public static List<string> PipeCells(string line)
    {
        var row = line.TrimSpaces();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && !row.EndsWith("\\|", StringComparison.Ordinal)) row = row[..^1];
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        var escaped = false;
        foreach (var character in row)
        {
            if (escaped)
            {
                // `\|` is a literal pipe; other escapes are left for the inline parser.
                if (character != '|') cell.Append('\\');
                cell.Append(character);
                escaped = false;
            }
            else if (character == '\\') escaped = true;
            else if (character == '`')
            {
                inCode = !inCode;
                cell.Append(character);
            }
            else if (character == '|' && !inCode)
            {
                cells.Add(cell.ToString().TrimSpaces());
                cell.Clear();
            }
            else cell.Append(character);
        }
        if (escaped) cell.Append('\\');
        cells.Add(cell.ToString().TrimSpaces());
        return cells;
    }

    private static List<string> Fitted(List<string> cells, int count) =>
        cells.Count >= count ? cells.Take(count).ToList() : cells.Concat(Enumerable.Repeat("", count - cells.Count)).ToList();

    /// <summary>The column spans of a line of dashes such as <c>----  -------</c>.</summary>
    public static List<(int Start, int End)>? DashColumns(string line)
    {
        if (!line.All(c => c == '-' || c == ' ') || !line.Contains('-')) return null;
        var columns = new List<(int, int)>();
        var index = 0;
        while (index < line.Length)
        {
            if (line[index] == '-')
            {
                var start = index;
                while (index < line.Length && line[index] == '-') index++;
                columns.Add((start, index));
            }
            else index++;
        }
        return columns;
    }

    /// <summary>Pandoc's simple tables: a header line over dashes, one per column.</summary>
    private static int? SimpleTable(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        if (!(start + 1 < lines.Count) || lines[start].IsBlank()) return null;
        var columns = DashColumns(lines[start + 1]);
        if (columns is null || columns.Count < 2) return null;
        var headerLine = lines[start];
        var header = Cells(headerLine, columns);
        var alignments = Enumerable.Range(0, columns.Count).Select(c => AlignmentOf(headerLine, columns, c)).ToList();
        var rows = new List<List<string>>();
        var end = start + 2;
        while (end < lines.Count && !lines[end].IsBlank() && DashColumns(lines[end]) is null)
        {
            rows.Add(Cells(lines[end], columns));
            end++;
        }
        // A closing line of dashes is optional.
        if (end < lines.Count && DashColumns(lines[end]) is not null) end++;
        if (rows.Count == 0) return null;
        var table = new MarkdownTable<string> { Header = header, Rows = rows, Alignments = alignments };
        return Caption(table, lines, end, blocks);
    }

    /// <summary>Pandoc's multi-line tables: dashes above and below, rows separated by blank lines.</summary>
    private static int? MultilineTable(List<string> lines, int start, List<MarkdownBlock<string>> blocks)
    {
        var top = DashColumns(lines[start]);
        if (top is null || top.Count == 0 || top[0].End - top[0].Start < 3) return null;
        if (!(start + 1 < lines.Count) || lines[start + 1].IsBlank() || DashColumns(lines[start + 1]) is not null) return null;
        // Header lines, then the dashes that set the columns.
        var separator = start + 1;
        while (separator < lines.Count && !lines[separator].IsBlank() && DashColumns(lines[separator]) is null) separator++;
        if (separator >= lines.Count) return null;
        var columns = DashColumns(lines[separator]);
        if (columns is null || columns.Count < 2) return null;
        // The closing dashes must be followed by a blank line or the end.
        var close = separator + 1;
        while (close < lines.Count && DashColumns(lines[close]) is null) close++;
        if (close >= lines.Count) return null;
        if (!(close + 1 == lines.Count || lines[close + 1].IsBlank() || lines[close + 1].StartsWith("Table:", StringComparison.Ordinal) || lines[close + 1].StartsWith(':')))
            return null;

        List<string> JoinedCells(List<string> rowLines)
        {
            var pieces = rowLines.Select(l => Cells(l, columns)).ToList();
            return Enumerable.Range(0, columns.Count)
                .Select(column => pieces.Select(p => p[column]).Where(c => c.Length > 0).Join("\n")).ToList();
        }

        var headerLines = lines.GetRange(start + 1, separator - (start + 1));
        var header = JoinedCells(headerLines);
        var alignments = Enumerable.Range(0, columns.Count).Select(c => AlignmentOf(headerLines.Count > 0 ? headerLines[0] : "", columns, c)).ToList();
        var rows = new List<List<string>>();
        var row = new List<string>();
        for (var i = separator + 1; i < close; i++)
        {
            if (lines[i].IsBlank())
            {
                if (row.Count > 0) rows.Add(JoinedCells(row));
                row = [];
            }
            else row.Add(lines[i]);
        }
        if (row.Count > 0) rows.Add(JoinedCells(row));
        double total = columns.Sum(c => c.End - c.Start);
        var table = new MarkdownTable<string>
        {
            Header = header, Rows = rows, Alignments = alignments,
            Widths = columns.Select(c => (c.End - c.Start) / Math.Max(total, 1)).ToList(),
        };
        return Caption(table, lines, close + 1, blocks);
    }

    /// <summary>The text of each column of <paramref name="line"/>, cut where the next column's dashes begin.</summary>
    public static List<string> Cells(string line, List<(int Start, int End)> columns)
    {
        var result = new List<string>();
        for (var index = 0; index < columns.Count; index++)
        {
            var start = index == 0 ? 0 : columns[index].Start;
            var end = index + 1 < columns.Count ? columns[index + 1].Start : line.Length;
            result.Add(start < line.Length && start < end ? line[start..Math.Min(end, line.Length)].TrimSpaces() : "");
        }
        return result;
    }

    /// <summary>How a simple table's header sits over its dashes sets the column's alignment.</summary>
    public static MarkdownAlignment AlignmentOf(string header, List<(int Start, int End)> columns, int column)
    {
        var span = columns[column];
        if (span.Start >= header.Length) return MarkdownAlignment.None;
        var end = Math.Min(span.End, header.Length);
        var text = header[span.Start..end];
        var flushLeft = text.Length > 0 && text[0] != ' ';
        var flushRight = span.End <= header.Length && header[span.End - 1] != ' ';
        return (flushLeft, flushRight) switch
        {
            (true, true) => MarkdownAlignment.None,
            (true, false) => MarkdownAlignment.Left,
            (false, true) => MarkdownAlignment.Right,
            _ => text.Any(c => c != ' ') ? MarkdownAlignment.Center : MarkdownAlignment.None,
        };
    }

    private static readonly Regex CaptionPattern = new(@"^ {0,3}(?:Table)?:[ \t]+(.*)$");

    /// <summary>Takes a <c>Table: caption</c> (or <c>: caption</c>) after the table, if there is one.</summary>
    private static int Caption(MarkdownTable<string> table, List<string> lines, int end, List<MarkdownBlock<string>> blocks)
    {
        var next = end;
        var line = next;
        if (line < lines.Count && lines[line].IsBlank()) line++;
        Match match;
        if (line < lines.Count && (match = CaptionPattern.Match(lines[line])).Success)
        {
            var caption = new List<string> { match.Groups[1].Value };
            next = line + 1;
            while (next < lines.Count && !lines[next].IsBlank())
            {
                caption.Add(lines[next].TrimSpaces());
                next++;
            }
            table.Caption = caption.Join(" ");
        }
        blocks.Add(new MarkdownBlock<string>.Table(table));
        return next;
    }

    private static readonly Regex CaptionParagraph = new(@"^(?:Table)?:[ \t]+((?s).*)$");

    /// <summary>A <c>Table: caption</c> paragraph right before a table belongs to it.</summary>
    private static List<MarkdownBlock<string>> AttachingCaptions(List<MarkdownBlock<string>> blocks)
    {
        var result = new List<MarkdownBlock<string>>();
        foreach (var block in blocks)
        {
            Match match;
            if (block is MarkdownBlock<string>.Table { Value: { Caption: null } table }
                && result.Count > 0 && result[^1] is MarkdownBlock<string>.Paragraph paragraph
                && (match = CaptionParagraph.Match(paragraph.Text)).Success)
            {
                table.Caption = match.Groups[1].Value.Replace("\n", " ");
                result[^1] = block;
            }
            else result.Add(block);
        }
        return result;
    }

    // MARK: Inline content

    private MarkdownBlock<MarkdownText> Resolve(MarkdownBlock<string> block)
    {
        switch (block)
        {
            case MarkdownBlock<string>.Heading heading:
            {
                var text = Inlines(heading.Text);
                var id = UniqueId(heading.Id.Length == 0 ? Identifier(text.PlainText()) : heading.Id);
                return new MarkdownBlock<MarkdownText>.Heading(heading.Level, text, id);
            }
            case MarkdownBlock<string>.Quote quote:
                return new MarkdownBlock<MarkdownText>.Quote(quote.Blocks.Select(Resolve).ToList());
            case MarkdownBlock<string>.ListBlock list:
                return new MarkdownBlock<MarkdownText>.ListBlock(new MarkdownList<MarkdownText>
                {
                    Ordered = list.List.Ordered, Start = list.List.Start, Tight = list.List.Tight,
                    Items = list.List.Items.Select(item => item.Select(Resolve).ToList()).ToList(),
                });
            case MarkdownBlock<string>.Definitions definitions:
                return new MarkdownBlock<MarkdownText>.Definitions(definitions.Items.Select(item => new MarkdownDefinition<MarkdownText>
                {
                    Term = Inlines(item.Term),
                    Definitions = item.Definitions.Select(blocks => blocks.Select(Resolve).ToList()).ToList(),
                }).ToList());
            default:
                // Leaf blocks: their text is inline content.
                return block.Map(Inlines);
        }
    }

    private string UniqueId(string baseId)
    {
        if (baseId.Length == 0) baseId = "section";
        var id = baseId;
        var suffix = 1;
        while (_headingIds.Contains(id))
        {
            id = $"{baseId}-{suffix}";
            suffix++;
        }
        _headingIds.Add(id);
        return id;
    }

    /// <summary>Pandoc's identifiers: "An h2 header" becomes "an-h2-header".</summary>
    public static string Identifier(string text)
    {
        var id = new StringBuilder();
        foreach (var character in text.ToLowerInvariant())
        {
            if (char.IsLetter(character) || char.IsNumber(character) || character is '_' or '-' or '.') id.Append(character);
            else if (char.IsWhiteSpace(character)) id.Append('-');
        }
        var s = id.ToString();
        var skip = 0;
        while (skip < s.Length && !char.IsLetter(s[skip])) skip++;
        return s[skip..];
    }

    internal MarkdownText Inlines(string source) => new InlineScanner(source, this).Parse();

    internal (string Url, string? Title)? Reference(string label) =>
        _references.TryGetValue(NormalizedLabel(label), out var found) ? found : null;

    /// <summary>The number of the footnote called <paramref name="label"/>, numbering it on first use.</summary>
    internal int? FootnoteNumber(string label)
    {
        var key = label.ToLowerInvariant();
        if (_footnoteNumbers.TryGetValue(key, out var number)) return number;
        if (!_footnoteSources.TryGetValue(key, out var source)) return null;
        return AddFootnote(source, key);
    }

    /// <summary>A Pandoc inline footnote, <c>^[text]</c>.</summary>
    internal int InlineFootnote(string text) => AddFootnote([new MarkdownBlock<string>.Paragraph(text)], null);

    private int AddFootnote(List<MarkdownBlock<string>> source, string? key)
    {
        _footnotes.Add([]);
        var number = _footnotes.Count;
        if (key is not null) _footnoteNumbers[key] = number;
        _footnotes[number - 1] = source.Select(Resolve).ToList();
        return number;
    }

    private static string ExpandingLeadingTabs(string line)
    {
        if (!line.Contains('\t')) return line;
        var result = new StringBuilder();
        var column = 0;
        var leading = true;
        foreach (var character in line)
        {
            if (leading && character == '\t')
            {
                var spaces = 4 - column % 4;
                result.Append(' ', spaces);
                column += spaces;
            }
            else
            {
                if (character != ' ') leading = false;
                result.Append(character);
                column++;
            }
        }
        return result.ToString();
    }
}
