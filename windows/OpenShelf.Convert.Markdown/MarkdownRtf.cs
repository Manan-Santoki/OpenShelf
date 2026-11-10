using System.Text;
using ImageMagick;

namespace OpenShelf.Convert.Markdown;

/// <summary>Writes a Markdown document as RTF directly from the model: headings, bold/italic/code, lists, links, quotes, rules,
/// tables (as RTF tables), footnotes (as real RTF footnotes) and pictures. The Mac gets its RTF from AppKit rich text; this is
/// the Windows counterpart of <c>MarkdownRichText</c>.</summary>
public sealed class MarkdownRtf
{
    private readonly string? _baseFolder;
    private readonly PaperSize _paper;
    private readonly StringBuilder _out = new();
    private MarkdownDocument _document = null!;
    private readonly HashSet<int> _emittedFootnotes = [];
    private bool _inFootnote;

    private const int BodySize = 11;
    private static readonly int[] HeadingSizes = [22, 17, 14, 12, 11, 11];
    private static readonly string[] Bullets = ["•", "◦", "▪"];

    private MarkdownRtf(string? baseFolder, PaperSize paper)
    {
        _baseFolder = baseFolder;
        _paper = paper;
    }

    private int PageWidth => _paper == PaperSize.Letter ? 12240 : 11906;
    private int PageHeight => _paper == PaperSize.Letter ? 15840 : 16838;
    private int TextWidth => PageWidth - 2 * 1440;

    public static string Render(MarkdownDocument document, string? baseFolder, PaperSize? paper = null) =>
        new MarkdownRtf(baseFolder, paper ?? PaperSizes.ForCurrentRegion()).RenderDocument(document);

    public static void Write(MarkdownDocument document, string? baseFolder, string path, PaperSize? paper = null) =>
        File.WriteAllText(path, Render(document, baseFolder, paper), Encoding.ASCII);

    private sealed record Ctx
    {
        public int Indent { get; init; }
        /// <summary>Type size in points.</summary>
        public double Size { get; init; } = BodySize;
        public int Color { get; init; } = 1;
        public bool Tight { get; init; }
        public bool Quote { get; init; }
        public int ListLevel { get; init; } = -1;
        public string Align { get; init; } = "";
        public int Width { get; init; }
    }

    private string RenderDocument(MarkdownDocument document)
    {
        _document = document;
        _out.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1");
        _out.Append(@"{\fonttbl{\f0\fswiss\fcharset0 Calibri;}{\f1\fmodern\fcharset0 Consolas;}{\f2\froman\fcharset0 Cambria Math;}}");
        _out.Append(@"{\colortbl;\red28\green28\blue31;\red204\green204\blue209;\red0\green92\blue191;\red102\green102\blue110;\red242\green242\blue245;}");
        _out.Append($@"{{\info{{\title {Esc(document.Title ?? "")}}}}}");
        _out.Append($@"\paperw{PageWidth}\paperh{PageHeight}\margl1440\margr1440\margt1440\margb1440\widowctrl\ftnbj");
        _out.Append('\n');
        var context = new Ctx { Width = TextWidth };
        if (document.Title is { } title)
            _out.Append($@"\pard\plain\f0\qc\sa360\cf1\b\fs48 {Esc(title)}\par").Append('\n');
        Blocks(document.Blocks, context);
        _out.Append('}');
        return _out.ToString();
    }

    // MARK: Text

    private static string Esc(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '{': builder.Append(@"\{"); break;
                case '}': builder.Append(@"\}"); break;
                case '\n': builder.Append(@"\line "); break;
                case '\t': builder.Append(@"\tab "); break;
                case '\r': break;
                case < ' ': break;
                case < (char)127: builder.Append(c); break;
                default: builder.Append(@"\u").Append((short)c).Append('?'); break;
            }
        }
        return builder.ToString();
    }

    private static int HalfPoints(double points) => (int)Math.Round(points * 2);

    // MARK: Blocks

    private void Blocks(List<MarkdownBlock<MarkdownText>> blocks, Ctx context)
    {
        foreach (var block in blocks) Block(block, context);
    }

    private string ParagraphStart(Ctx c, int before = 0, int? after = null, string extra = "") =>
        $@"\pard\plain\f0\fs{HalfPoints(c.Size)}\cf{c.Color}{c.Align}\sb{before}\sa{after ?? (c.Tight ? 40 : 160)}\sl276\slmult1"
        + (c.Indent > 0 ? $@"\li{c.Indent}" : "")
        + (c.Quote ? $@"\brdrl\brdrs\brdrw30\brsp140\brdrcf2" : "")
        + extra + " ";

    private void Block(MarkdownBlock<MarkdownText> block, Ctx context)
    {
        switch (block)
        {
            case MarkdownBlock<MarkdownText>.Heading h:
            {
                var size = HeadingSizes[Math.Min(Math.Max(h.Level, 1), 6) - 1] * context.Size / BodySize;
                var heading = context with { Size = size };
                _out.Append(ParagraphStart(heading, before: h.Level <= 2 ? 280 : 200, after: 120, extra: @"\keepn\b" + (h.Level == 6 ? @"\i" : "")));
                Inlines(h.Text, heading, new Format(Bold: true, Italic: h.Level == 6));
                _out.Append(@"\par").Append('\n');
                break;
            }
            case MarkdownBlock<MarkdownText>.Paragraph p:
            {
                if (p.Text.FigureImage() is { } figure)
                {
                    var centered = context with { Align = @"\qc" };
                    var picture = Picture(figure.Source, context);
                    var caption = figure.Alt.PlainText();
                    if (picture is not null)
                    {
                        _out.Append(ParagraphStart(centered, after: caption.Length > 0 ? 60 : null, extra: @"\keep"));
                        _out.Append(picture).Append(@"\par").Append('\n');
                        if (caption.Length > 0)
                        {
                            var captionContext = centered with { Size = context.Size * 0.9, Color = 4 };
                            _out.Append(ParagraphStart(captionContext));
                            Inlines(figure.Alt, captionContext, new Format(Italic: true));
                            _out.Append(@"\par").Append('\n');
                        }
                    }
                    else
                    {
                        _out.Append(ParagraphStart(centered));
                        _out.Append(@"\i ").Append(Esc($"[{(caption.Length == 0 ? "image" : caption)}]")).Append(@"\par").Append('\n');
                    }
                }
                else
                {
                    _out.Append(ParagraphStart(context));
                    Inlines(p.Text, context, new Format());
                    _out.Append(@"\par").Append('\n');
                }
                break;
            }
            case MarkdownBlock<MarkdownText>.Code c:
            {
                var text = c.Source.Replace("\t", "    ");
                var size = 9 * context.Size / BodySize;
                _out.Append($@"\pard\plain\f1\fs{HalfPoints(size)}\cf1\cbpat5\sb40\sa{(context.Tight ? 80 : 200)}\li{context.Indent}\ri0\sl264\slmult1\keep ");
                _out.Append(Esc(text.Length == 0 ? " " : text)).Append(@"\par").Append('\n');
                break;
            }
            case MarkdownBlock<MarkdownText>.Math m:
            {
                var centered = context with { Align = @"\qc", Size = context.Size * 1.15 };
                _out.Append(ParagraphStart(centered, before: 40, after: 200));
                _out.Append(@"\f2\i ").Append(Esc(MarkdownMath.Unicode(m.Tex))).Append(@"\par").Append('\n');
                break;
            }
            case MarkdownBlock<MarkdownText>.Quote q:
                Blocks(q.Blocks, context with { Indent = context.Indent + 360, Quote = true, Color = 4, Width = context.Width - 360 });
                break;
            case MarkdownBlock<MarkdownText>.ListBlock l:
                ListBlock(l.List, context);
                break;
            case MarkdownBlock<MarkdownText>.Table t:
                Table(t.Value, context);
                break;
            case MarkdownBlock<MarkdownText>.Definitions d:
                foreach (var item in d.Items)
                {
                    _out.Append(ParagraphStart(context, after: 40, extra: @"\keepn\b"));
                    Inlines(item.Term, context, new Format(Bold: true));
                    _out.Append(@"\par").Append('\n');
                    foreach (var definition in item.Definitions)
                        Blocks(definition, context with { Indent = context.Indent + 720, Tight = definition.Count == 1 || context.Tight });
                }
                break;
            case MarkdownBlock<MarkdownText>.Lines lines:
            {
                _out.Append(ParagraphStart(context));
                for (var i = 0; i < lines.Items.Count; i++)
                {
                    if (i > 0) _out.Append(@"\line ");
                    Inlines(lines.Items[i], context, new Format());
                }
                _out.Append(@"\par").Append('\n');
                break;
            }
            default:
                _out.Append($@"\pard\plain\fs8\sb120\sa240\li{context.Indent}\brdrb\brdrs\brdrw10\brsp1\brdrcf2 \par").Append('\n');
                break;
        }
    }

    private void ListBlock(MarkdownList<MarkdownText> list, Ctx context)
    {
        var level = Math.Min(context.ListLevel + 1, 8);
        var inside = context with { ListLevel = level, Indent = context.Indent + 360, Tight = list.Tight };
        var number = list.Start;
        foreach (var item in list.Items)
        {
            var marker = list.Ordered ? $"{number++}." : Bullets[level % 3];
            var rest = item;
            var first = item.Count > 0 ? item[0] as MarkdownBlock<MarkdownText>.Paragraph : null;
            var hanging = $@"\fi-360\tx{inside.Indent}";
            if (first is not null && first.Text.FigureImage() is null)
            {
                _out.Append(ParagraphStart(inside, extra: hanging));
                _out.Append(Esc(marker)).Append(@"\tab ");
                Inlines(first.Text, inside, new Format());
                _out.Append(@"\par").Append('\n');
                rest = item.Skip(1).ToList();
            }
            else
            {
                _out.Append(ParagraphStart(inside, extra: hanging));
                _out.Append(Esc(marker)).Append(@"\par").Append('\n');
            }
            Blocks(rest, inside);
        }
    }

    private void Table(MarkdownTable<MarkdownText> table, Ctx context)
    {
        if (table.Caption is { } caption)
        {
            var captionContext = context with { Align = @"\qc", Color = 4 };
            _out.Append(ParagraphStart(captionContext, before: 120, after: 80, extra: @"\keepn"));
            Inlines(caption, captionContext, new Format(Italic: true));
            _out.Append(@"\par").Append('\n');
        }
        var columns = Math.Max(table.ColumnCount, 1);
        var available = Math.Max(context.Width - context.Indent, 1440);
        var fractions = table.Widths ?? Enumerable.Repeat(1.0 / columns, columns).ToList();
        var edges = new List<int>();
        var running = context.Indent;
        foreach (var fraction in fractions)
        {
            running += (int)(available * fraction);
            edges.Add(running);
        }

        var rows = new List<(List<MarkdownText> Cells, bool IsHeader)>();
        if (table.Header is { } header && header.Any(c => c.Count > 0)) rows.Add((header, true));
        rows.AddRange(table.Rows.Select(r => (r, false)));
        var cellContext = context with { Tight = true, Indent = 0 };
        foreach (var row in rows)
        {
            _out.Append($@"\trowd\trgaph108\trleft{context.Indent}" + (row.IsHeader ? @"\trhdr" : ""));
            for (var column = 0; column < table.ColumnCount; column++)
            {
                var border = row.IsHeader ? @"\clbrdrb\brdrs\brdrw20\brdrcf1" : @"\clbrdrb\brdrs\brdrw8\brdrcf2";
                _out.Append($@"{border}\clvertalt\cellx{edges[column]}");
            }
            _out.Append('\n');
            for (var column = 0; column < table.ColumnCount; column++)
            {
                var align = table.Alignments[column] switch { MarkdownAlignment.Center => @"\qc", MarkdownAlignment.Right => @"\qr", _ => @"\ql" };
                _out.Append($@"\pard\plain\intbl\f0\fs{HalfPoints(context.Size * 0.95)}\cf1{align}\sb40\sa40 ");
                if (column < row.Cells.Count) Inlines(row.Cells[column], cellContext, new Format(Bold: row.IsHeader));
                _out.Append(@"\cell").Append('\n');
            }
            _out.Append(@"\row").Append('\n');
        }
        // Word has no space below a table; a small empty paragraph makes some.
        _out.Append($@"\pard\plain\fs6\sa80 \par").Append('\n');
    }

    // MARK: Inline content

    private readonly record struct Format(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, bool Link = false);

    private void Inlines(MarkdownText text, Ctx context, Format format)
    {
        foreach (var inline in text) Inline(inline, context, format);
    }

    private void Styled(string text, Format format, string extra = "")
    {
        var open = new StringBuilder();
        if (format.Code) open.Append($@"\f1\fs{HalfPoints(9.5)}\cbpat5 ");
        if (format.Bold) open.Append(@"\b ");
        if (format.Italic) open.Append(@"\i ");
        if (format.Strike) open.Append(@"\strike ");
        if (format.Link) open.Append(@"\cf3\ul ");
        open.Append(extra);
        if (open.Length == 0) _out.Append(Esc(text));
        else _out.Append('{').Append(open).Append(Esc(text)).Append('}');
    }

    private void Inline(MarkdownInline inline, Ctx context, Format format)
    {
        switch (inline)
        {
            case MarkdownInline.Text t: Styled(t.Value, format); break;
            case MarkdownInline.Emphasis e: Inlines(e.Content, context, format with { Italic = true }); break;
            case MarkdownInline.Strong s: Inlines(s.Content, context, format with { Bold = true }); break;
            case MarkdownInline.Strikethrough s: Inlines(s.Content, context, format with { Strike = true }); break;
            case MarkdownInline.Code c: Styled(c.Value, format with { Code = true }); break;
            case MarkdownInline.Math m: Styled(MarkdownMath.Unicode(m.Tex), format with { Italic = true }, @"\f2 "); break;
            case MarkdownInline.Link l:
                if (!MarkdownHtml.IsSafe(l.Url) || l.Url.StartsWith('#')) { Inlines(l.Content, context, l.Url.StartsWith('#') ? format with { Link = true } : format); break; }
                _out.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(Esc(l.Url.Replace("\"", "%22"))).Append(@"""}}{\fldrslt{");
                Inlines(l.Content, context, format with { Link = true });
                _out.Append("}}}");
                break;
            case MarkdownInline.Image i:
            {
                var picture = Picture(i.Source, context);
                if (picture is not null) _out.Append(picture);
                else
                {
                    var description = i.Alt.PlainText();
                    Styled($"[{(description.Length == 0 ? "image" : description)}]", format with { Italic = true });
                }
                break;
            }
            case MarkdownInline.Footnote f: Footnote(f.Number); break;
            case MarkdownInline.LineBreak: _out.Append(@"\line "); break;
            default: _out.Append(' '); break;
        }
    }

    private void Footnote(int number)
    {
        if (_inFootnote || number < 1 || number > _document.Footnotes.Count || !_emittedFootnotes.Add(number))
        {
            _out.Append($@"{{\super {number}}}");
            return;
        }
        _inFootnote = true;
        var context = new Ctx { Size = 9, Color = 1, Tight = true, Width = TextWidth };
        _out.Append(@"{\super\chftn{\footnote ");
        var first = true;
        foreach (var block in _document.Footnotes[number - 1])
        {
            if (first)
            {
                _out.Append($@"\pard\plain\f0\fs18\sa60 {{\super\chftn }} ");
                if (block is MarkdownBlock<MarkdownText>.Paragraph p)
                {
                    Inlines(p.Text, context, new Format());
                    _out.Append(@"\par ");
                    first = false;
                    continue;
                }
                _out.Append(@"\par ");
                first = false;
            }
            Block(block, context);
        }
        if (first) _out.Append($@"\pard\plain\f0\fs18 {{\super\chftn }}\par ");
        _out.Append("}}");
        _inFootnote = false;
    }

    /// <summary>The picture as an RTF group, or null when it is not on this PC.</summary>
    private string? Picture(string source, Ctx context)
    {
        if (_inFootnote || MarkdownImages.LocalPath(source, _baseFolder) is not { } path) return null;
        var picture = Pictures.Embeddable(path);
        if (picture is null) return null;
        var data = picture.Data;
        var blip = picture.Extension == "jpeg" ? @"\jpegblip" : @"\pngblip";
        int pixelWidth, pixelHeight;
        try
        {
            if (picture.Extension == "gif")
            {
                using var gif = new MagickImage(path);
                data = gif.ToByteArray(MagickFormat.Png);
            }
            var info = new MagickImageInfo(data);
            pixelWidth = (int)info.Width;
            pixelHeight = (int)info.Height;
        }
        catch (MagickException) { return null; }
        var widthTwips = picture.Width / 635;
        var heightTwips = picture.Height / 635;
        var maxWidth = Math.Max(context.Width - context.Indent, 1440);
        var scale = Math.Min(1, Math.Min(maxWidth / widthTwips, 9500 / heightTwips));
        var goalWidth = (int)(widthTwips * scale);
        var goalHeight = (int)(heightTwips * scale);
        var hex = new StringBuilder(data.Length * 2 + 64);
        hex.Append($@"{{\pict{blip}\picw{pixelWidth}\pich{pixelHeight}\picwgoal{goalWidth}\pichgoal{goalHeight} ");
        for (var i = 0; i < data.Length; i++)
        {
            hex.Append(data[i].ToString("x2"));
            if (i % 64 == 63) hex.Append('\n');
        }
        hex.Append('}');
        return hex.ToString();
    }
}
