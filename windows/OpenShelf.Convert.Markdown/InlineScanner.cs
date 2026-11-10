using System.Text;
using System.Text.RegularExpressions;

namespace OpenShelf.Convert.Markdown;

/// <summary>Turns a block's text into emphasis, links, code, math and so on. Port of InlineScanner.</summary>
internal sealed class InlineScanner
{
    private abstract class Piece
    {
        public sealed class Node(MarkdownInline inline) : Piece { public MarkdownInline Inline { get; } = inline; }
        public sealed class Delimiter(char character, int count, bool canOpen, bool canClose) : Piece
        {
            public char Character { get; } = character;
            public int Count { get; set; } = count;
            public bool CanOpen { get; } = canOpen;
            public bool CanClose { get; } = canClose;
            public int Original { get; } = count;
        }

        public MarkdownInline AsInline() => this switch
        {
            Node n => n.Inline,
            Delimiter d => new MarkdownInline.Text(new string(d.Character, d.Count)),
            _ => throw new InvalidOperationException(),
        };
    }

    private readonly MarkdownParser _resolver;
    private readonly string _source;
    private readonly char[] _characters;
    private int _position;
    private readonly List<Piece> _pieces = [];
    private readonly StringBuilder _text = new();

    public InlineScanner(string source, MarkdownParser resolver)
    {
        _source = source;
        _characters = source.ToCharArray();
        _resolver = resolver;
    }

    public MarkdownText Parse()
    {
        while (_position < _characters.Length)
        {
            var character = _characters[_position];
            switch (character)
            {
                case '\\': Backslash(); break;
                case '`': CodeSpan(); break;
                case '$': InlineMath(); break;
                case '!' when Peek(1) == '[': if (!Link(image: true)) Literal(); break;
                case '[': if (!FootnoteReference() && !Link(image: false)) Literal(); break;
                case '^' when Peek(1) == '[': if (!InlineFootnote()) Literal(); break;
                case '<': AngleBracket(); break;
                case '*' or '_' or '~': DelimiterRun(); break;
                case '\n': LineEnd(); break;
                case '&': Entity(); break;
                case 'h' or 'w': if (!BareUrl()) SmartText(); break;
                default: SmartText(); break;
            }
        }
        Flush();
        ProcessEmphasis(0);
        return Merged(_pieces.Select(p => p.AsInline()));
    }

    private char? Peek(int offset)
    {
        var index = _position + offset;
        return index >= 0 && index < _characters.Length ? _characters[index] : null;
    }

    private void Flush()
    {
        if (_text.Length == 0) return;
        _pieces.Add(new Piece.Node(new MarkdownInline.Text(_text.ToString())));
        _text.Clear();
    }

    private void Append(MarkdownInline inline)
    {
        Flush();
        _pieces.Add(new Piece.Node(inline));
    }

    private void Literal()
    {
        _text.Append(_characters[_position]);
        _position++;
    }

    private string Slice(int start, int end) => _source.Substring(start, end - start);

    private int IndexOf(char c, int from) => from < _characters.Length ? Array.IndexOf(_characters, c, from) : -1;

    private void Backslash()
    {
        if (Peek(1) is not { } next) { Literal(); return; }
        if (next == '\n')
        {
            Append(new MarkdownInline.LineBreak());
            _position += 2;
        }
        else if (next < 128 && MdText.IsPunctuationOrSymbol(next))
        {
            _text.Append(next);
            _position += 2;
        }
        else Literal();
    }

    private void CodeSpan()
    {
        var start = _position;
        var ticks = 0;
        while (Peek(ticks) == '`') ticks++;
        var search = _position + ticks;
        while (search < _characters.Length)
        {
            if (_characters[search] == '`')
            {
                var run = 0;
                while (search + run < _characters.Length && _characters[search + run] == '`') run++;
                if (run == ticks)
                {
                    var code = Slice(start + ticks, search).Replace('\n', ' ');
                    if (code.Length >= 2 && code.StartsWith(' ') && code.EndsWith(' ') && code.Any(c => c != ' ')) code = code[1..^1];
                    Append(new MarkdownInline.Code(code));
                    _position = search + run;
                    // Pandoc's `code`{.language} attributes add nothing here.
                    SkipAttributes();
                    return;
                }
                search += run;
            }
            else search++;
        }
        _text.Append('`', ticks);
        _position += ticks;
    }

    /// <summary><c>$x$</c> and <c>$$x$$</c>. A <c>$</c> before a space or a digit after the closing one means money, not math.</summary>
    private void InlineMath()
    {
        if (Peek(1) == '$')
        {
            var find = _position + 2;
            while (find + 1 < _characters.Length)
            {
                if (_characters[find] == '$' && _characters[find + 1] == '$')
                {
                    var tex = Slice(_position + 2, find).Trim();
                    if (tex.Length == 0) break;
                    Append(new MarkdownInline.Math(tex));
                    _position = find + 2;
                    return;
                }
                find++;
            }
            _text.Append("$$");
            _position += 2;
            return;
        }
        if (Peek(1) is not { } next || char.IsWhiteSpace(next)) { Literal(); return; }
        var search = _position + 1;
        while (search < _characters.Length)
        {
            var character = _characters[search];
            if (character == '\\') { search += 2; continue; }
            if (character == '\n' && search + 1 < _characters.Length && _characters[search + 1] == '\n') break;
            if (character == '$')
            {
                var before = _characters[search - 1];
                var after = search + 1 < _characters.Length ? _characters[search + 1] : ' ';
                if (!char.IsWhiteSpace(before) && !char.IsNumber(after))
                {
                    Append(new MarkdownInline.Math(Slice(_position + 1, search)));
                    _position = search + 1;
                    return;
                }
            }
            search++;
        }
        Literal();
    }

    private bool FootnoteReference()
    {
        if (Peek(1) != '^') return false;
        var close = IndexOf(']', _position);
        if (close < 0) return false;
        var label = Slice(_position + 2, close);
        if (label.Length == 0 || label.Any(char.IsWhiteSpace)) return false;
        if (_resolver.FootnoteNumber(label) is not { } number) return false;
        Append(new MarkdownInline.Footnote(number));
        _position = close + 1;
        return true;
    }

    private bool InlineFootnote()
    {
        if (MatchingBracket(_position + 1) is not { } close) return false;
        var content = Slice(_position + 2, close);
        Append(new MarkdownInline.Footnote(_resolver.InlineFootnote(content)));
        _position = close + 1;
        return true;
    }

    /// <summary>The index of the <c>]</c> that closes the <c>[</c> at <paramref name="open"/>, skipping code and escapes.</summary>
    private int? MatchingBracket(int open)
    {
        var depth = 0;
        var index = open;
        while (index < _characters.Length)
        {
            switch (_characters[index])
            {
                case '\\': index++; break;
                case '`':
                    var ticks = 0;
                    while (index + ticks < _characters.Length && _characters[index + ticks] == '`') ticks++;
                    var closing = new string('`', ticks);
                    var found = _source.IndexOf(closing, index + ticks, StringComparison.Ordinal);
                    index = found >= 0 ? found + ticks - 1 : index + ticks - 1;
                    break;
                case '[': depth++; break;
                case ']':
                    depth--;
                    if (depth == 0) return index;
                    break;
            }
            index++;
        }
        return null;
    }

    /// <summary><c>[text](url "title")</c>, <c>[text][ref]</c>, <c>[ref]</c> and their image forms.</summary>
    private bool Link(bool image)
    {
        var open = image ? _position + 1 : _position;
        if (MatchingBracket(open) is not { } close) return false;
        var label = Slice(open + 1, close);
        var after = close + 1;
        (string Url, string? Title)? destination = null;

        (string Url, string? Title, int End)? inline;
        int refClose;
        if (after < _characters.Length && _characters[after] == '(' && (inline = InlineDestination(after)) is not null)
        {
            destination = (inline.Value.Url, inline.Value.Title);
            after = inline.Value.End;
        }
        else if (after < _characters.Length && _characters[after] == '[' && (refClose = IndexOf(']', after)) >= 0)
        {
            var reference = Slice(after + 1, refClose);
            if (_resolver.Reference(reference.Length == 0 ? label : reference) is { } found)
            {
                destination = found;
                after = refClose + 1;
            }
        }
        else if (_resolver.Reference(label) is { } shortcut) destination = shortcut;
        else if (!image && after < _characters.Length && _characters[after] == '{')
        {
            // A Pandoc span, [text]{.class}: keep the text, drop the attributes.
            Flush();
            foreach (var piece in Nested(label)) _pieces.Add(new Piece.Node(piece));
            _position = after;
            SkipAttributes();
            return true;
        }
        if (destination is not { } target) return false;
        var content = Nested(label);
        Append(image
            ? new MarkdownInline.Image(target.Url, target.Title, content)
            : new MarkdownInline.Link(target.Url, target.Title, content));
        _position = after;
        SkipAttributes();
        return true;
    }

    private MarkdownText Nested(string source) => new InlineScanner(source, _resolver).Parse();

    private (string Url, string? Title, int End)? InlineDestination(int open)
    {
        var index = open + 1;
        void SkipSpace() { while (index < _characters.Length && char.IsWhiteSpace(_characters[index])) index++; }
        SkipSpace();
        var url = new StringBuilder();
        if (index < _characters.Length && _characters[index] == '<')
        {
            var close = IndexOf('>', index);
            if (close < 0) return null;
            url.Append(Slice(index + 1, close));
            index = close + 1;
        }
        else
        {
            var depth = 0;
            while (index < _characters.Length)
            {
                var character = _characters[index];
                if (char.IsWhiteSpace(character)) break;
                if (character == '\\' && index + 1 < _characters.Length)
                {
                    url.Append(_characters[index + 1]);
                    index += 2;
                    continue;
                }
                if (character == '(') depth++;
                if (character == ')')
                {
                    if (depth == 0) break;
                    depth--;
                }
                url.Append(character);
                index++;
            }
        }
        SkipSpace();
        string? title = null;
        if (index < _characters.Length)
        {
            char? closer = _characters[index] switch { '"' => '"', '\'' => '\'', '(' => ')', _ => null };
            if (closer is { } c)
            {
                var close = IndexOf(c, index + 1);
                if (close < 0) return null;
                title = Slice(index + 1, close);
                index = close + 1;
                SkipSpace();
            }
        }
        if (index >= _characters.Length || _characters[index] != ')') return null;
        return (url.ToString(), title, index + 1);
    }

    /// <summary>Skips Pandoc attributes, <c>{width=50%}</c> or <c>{.class}</c>, after a link, image or code.</summary>
    private void SkipAttributes()
    {
        if (Peek(0) != '{') return;
        var close = IndexOf('}', _position);
        if (close < 0) return;
        for (var i = _position; i < close; i++) if (_characters[i] == '\n') return;
        _position = close + 1;
    }

    private static readonly Regex AutoLink = new(@"\G<([A-Za-z][A-Za-z0-9+.-]{1,31}:[^\s<>]*)>");
    private static readonly Regex EmailLink = new(@"\G<([A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*)>");
    private static readonly Regex HtmlComment = new(@"\G<!--(?s).*?-->");
    private static readonly Regex HtmlTag = new(@"\G<\/?([A-Za-z][A-Za-z0-9-]*)(?:\s[^<>]*)?\/?>");

    private void AngleBracket()
    {
        Match match;
        if ((match = AutoLink.Match(_source, _position)).Success)
        {
            Append(new MarkdownInline.Link(match.Groups[1].Value, null, MarkdownText.OfString(match.Groups[1].Value)));
            _position += match.Length;
        }
        else if ((match = EmailLink.Match(_source, _position)).Success)
        {
            Append(new MarkdownInline.Link("mailto:" + match.Groups[1].Value, null, MarkdownText.OfString(match.Groups[1].Value)));
            _position += match.Length;
        }
        else if ((match = HtmlComment.Match(_source, _position)).Success) _position += match.Length;
        else if ((match = HtmlTag.Match(_source, _position)).Success)
        {
            // Raw HTML tags are dropped and their text kept; <br> still breaks the line.
            if (match.Groups[1].Value.Equals("br", StringComparison.OrdinalIgnoreCase)) Append(new MarkdownInline.LineBreak());
            _position += match.Length;
        }
        else Literal();
    }

    private static readonly Regex BareUrlPattern = new(@"\G(?:https?://|www\.)[^\s<]+");

    /// <summary>A URL typed without angle brackets, as GitHub links them.</summary>
    private bool BareUrl()
    {
        if (Peek(-1) is { } previous && !(char.IsWhiteSpace(previous) || "(*_".Contains(previous))) return false;
        var match = BareUrlPattern.Match(_source, _position);
        if (!match.Success) return false;
        var url = match.Value;
        // Trailing punctuation ends the sentence, not the URL; so does an unmatched ")".
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,:;!?'\"*_".Contains(last) || (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))) url = url[..^1];
            else break;
        }
        if (url.Length <= 4 || !url.Contains('.')) return false;
        Append(new MarkdownInline.Link(url.StartsWith("www.", StringComparison.Ordinal) ? "http://" + url : url, null, MarkdownText.OfString(url)));
        _position += url.Length;
        return true;
    }

    private static readonly Regex EntityPattern = new(@"\G&(#[0-9]{1,7}|#[xX][0-9A-Fa-f]{1,6}|[A-Za-z]{2,8});");

    private void Entity()
    {
        var match = EntityPattern.Match(_source, _position);
        if (match.Success)
        {
            var name = match.Groups[1].Value;
            string? decoded = null;
            if (name.StartsWith("#x", StringComparison.OrdinalIgnoreCase))
                decoded = ScalarString(int.Parse(name[2..], System.Globalization.NumberStyles.HexNumber));
            else if (name.StartsWith('#'))
                decoded = ScalarString(int.Parse(name[1..]));
            else if (Entities.TryGetValue(name, out var named)) decoded = named;
            if (decoded is not null)
            {
                _text.Append(decoded);
                _position += match.Length;
                return;
            }
        }
        Literal();
    }

    private static string? ScalarString(int value) => Rune.IsValid(value) ? new Rune(value).ToString() : null;

    private static readonly Dictionary<string, string> Entities = new()
    {
        ["amp"] = "&", ["lt"] = "<", ["gt"] = ">", ["quot"] = "\"", ["apos"] = "'", ["nbsp"] = " ",
        ["copy"] = "©", ["reg"] = "®", ["trade"] = "™", ["mdash"] = "—", ["ndash"] = "–", ["hellip"] = "…",
        ["laquo"] = "«", ["raquo"] = "»", ["ldquo"] = "“", ["rdquo"] = "”", ["lsquo"] = "‘", ["rsquo"] = "’",
        ["times"] = "×", ["divide"] = "÷", ["deg"] = "°", ["middot"] = "·", ["bull"] = "•", ["rarr"] = "→", ["larr"] = "←",
        ["para"] = "¶", ["sect"] = "§", ["euro"] = "€", ["pound"] = "£", ["yen"] = "¥", ["cent"] = "¢",
    };

    private void TrimTextSpaces()
    {
        var length = _text.Length;
        while (length > 0 && _text[length - 1] == ' ') length--;
        _text.Length = length;
    }

    private void LineEnd()
    {
        // Two spaces at the end of a line make a hard break.
        var hard = _text.Length >= 2 && _text[^1] == ' ' && _text[^2] == ' ';
        TrimTextSpaces();
        Append(hard ? new MarkdownInline.LineBreak() : new MarkdownInline.SoftBreak());
        _position++;
        while (Peek(0) == ' ') _position++;
    }

    /// <summary>Plain text, with straight quotes curled and <c>--</c>, <c>---</c> and <c>...</c> typeset.</summary>
    private void SmartText()
    {
        var character = _characters[_position];
        if (character == '-' && Peek(1) == '-')
        {
            if (Peek(2) == '-') { _text.Append('—'); _position += 3; }
            else { _text.Append('–'); _position += 2; }
        }
        else if (character == '.' && Peek(1) == '.' && Peek(2) == '.')
        {
            _text.Append('…');
            _position += 3;
        }
        else if (character == '"')
        {
            _text.Append(OpensQuote() ? '“' : '”');
            _position++;
        }
        else if (character == '\'')
        {
            if (Peek(-1) is { } previous && (char.IsLetter(previous) || char.IsNumber(previous))) _text.Append('’');
            else _text.Append(OpensQuote() ? '‘' : '’');
            _position++;
        }
        else Literal();
    }

    private bool OpensQuote()
    {
        var previous = Peek(-1);
        var next = Peek(1);
        var afterOpening = previous is not { } p || char.IsWhiteSpace(p) || "([{—–-/“‘".Contains(p);
        var beforeText = next is { } n && !char.IsWhiteSpace(n);
        return afterOpening && beforeText;
    }

    // MARK: Emphasis

    private void DelimiterRun()
    {
        var character = _characters[_position];
        var count = 0;
        while (Peek(count) == character) count++;
        if (character == '~' && count != 2)
        {
            _text.Append('~', count);
            _position += count;
            return;
        }
        var before = Peek(-1) ?? ' ';
        var after = Peek(count) ?? ' ';
        var leftFlanking = !char.IsWhiteSpace(after) && (!MdText.IsPunctuationOrSymbol(after) || char.IsWhiteSpace(before) || MdText.IsPunctuationOrSymbol(before));
        var rightFlanking = !char.IsWhiteSpace(before) && (!MdText.IsPunctuationOrSymbol(before) || char.IsWhiteSpace(after) || MdText.IsPunctuationOrSymbol(after));
        var canOpen = leftFlanking;
        var canClose = rightFlanking;
        if (character == '_')
        {
            canOpen = leftFlanking && (!rightFlanking || MdText.IsPunctuationOrSymbol(before));
            canClose = rightFlanking && (!leftFlanking || MdText.IsPunctuationOrSymbol(after));
        }
        Flush();
        _pieces.Add(new Piece.Delimiter(character, count, canOpen, canClose));
        _position += count;
    }

    /// <summary>CommonMark's emphasis algorithm: pairs closers with the nearest matching opener before them.</summary>
    private void ProcessEmphasis(int bottom)
    {
        var closer = bottom;
        while (closer < _pieces.Count)
        {
            if (_pieces[closer] is not Piece.Delimiter { CanClose: true } close) { closer++; continue; }
            var opener = closer - 1;
            var found = false;
            while (opener >= bottom)
            {
                if (_pieces[opener] is Piece.Delimiter { CanOpen: true } candidate && candidate.Character == close.Character)
                {
                    // The "multiple of 3" rule keeps ***a** b* sensible.
                    var oddMatch = (candidate.CanClose || close.CanOpen) && (candidate.Original + close.Original) % 3 == 0
                        && !(candidate.Original % 3 == 0 && close.Original % 3 == 0);
                    if (!oddMatch) { found = true; break; }
                }
                opener--;
            }
            if (!found)
            {
                if (!close.CanOpen) _pieces[closer] = new Piece.Node(new MarkdownInline.Text(new string(close.Character, close.Count)));
                closer++;
                continue;
            }
            var open = (Piece.Delimiter)_pieces[opener];
            var used = close.Character == '~' ? 2 : (open.Count >= 2 && close.Count >= 2 ? 2 : 1);
            var inner = Merged(_pieces.Skip(opener + 1).Take(closer - opener - 1).Select(p => p.AsInline()));
            MarkdownInline wrapped = close.Character == '~' ? new MarkdownInline.Strikethrough(inner)
                : used == 2 ? new MarkdownInline.Strong(inner)
                : new MarkdownInline.Emphasis(inner);
            open.Count -= used;
            close.Count -= used;
            _pieces.RemoveRange(opener + 1, closer - opener - 1);
            _pieces.Insert(opener + 1, new Piece.Node(wrapped));
            closer = opener + 2;
            if (open.Count == 0)
            {
                _pieces.RemoveAt(opener);
                closer--;
            }
            if (close.Count == 0) _pieces.RemoveAt(closer);
        }
    }

    /// <summary>Joins neighbouring text and leaves unmatched delimiters as text.</summary>
    internal static MarkdownText Merged(IEnumerable<MarkdownInline> inlines)
    {
        var result = new MarkdownText();
        foreach (var inline in inlines)
        {
            if (inline is MarkdownInline.Text next && result.Count > 0 && result[^1] is MarkdownInline.Text previous)
                result[^1] = new MarkdownInline.Text(previous.Value + next.Value);
            else if (inline is MarkdownInline.Text { Value.Length: 0 }) continue;
            else result.Add(inline);
        }
        return result;
    }
}
