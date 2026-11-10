using System.Text;

namespace OpenShelf.Convert.Markdown;

/// <summary>A parsed Markdown document: CommonMark with GitHub's tables, task lists and strikethrough, plus the Pandoc
/// extensions people reach for most: footnotes, definition lists, simple and multi-line tables, line blocks, math, smart
/// punctuation and a YAML title. Port of Markdown.swift.</summary>
public sealed class MarkdownDocument
{
    public string? Title { get; set; }
    public List<MarkdownBlock<MarkdownText>> Blocks { get; set; }
    /// <summary>Footnotes in the order they are first referenced; <c>Footnote(n)</c> is <c>Footnotes[n - 1]</c>.</summary>
    public List<List<MarkdownBlock<MarkdownText>>> Footnotes { get; set; }

    public MarkdownDocument(string? title, List<MarkdownBlock<MarkdownText>> blocks, List<List<MarkdownBlock<MarkdownText>>>? footnotes = null)
    {
        Title = title;
        Blocks = blocks;
        Footnotes = footnotes ?? [];
    }

    public static MarkdownDocument Parse(string source) => MarkdownParser.Parse(source);
}

/// <summary>Inline content of a block. Compares by value, like the Swift array of enums.</summary>
public sealed class MarkdownText : List<MarkdownInline>, IEquatable<MarkdownText>
{
    public MarkdownText() { }
    public MarkdownText(IEnumerable<MarkdownInline> items) : base(items) { }

    public bool Equals(MarkdownText? other) => other is not null && this.SequenceEqual(other);
    public override bool Equals(object? obj) => obj is MarkdownText other && Equals(other);
    public override int GetHashCode() => Count;

    public static MarkdownText Of(params MarkdownInline[] items) => new(items);
    public static MarkdownText OfString(string text) => new([new MarkdownInline.Text(text)]);

    /// <summary>The words without formatting, as a screen reader or a plain text file would have them.</summary>
    public string PlainText()
    {
        var builder = new StringBuilder();
        foreach (var inline in this)
        {
            switch (inline)
            {
                case MarkdownInline.Text t: builder.Append(t.Value); break;
                case MarkdownInline.Code c: builder.Append(c.Value); break;
                case MarkdownInline.Math m: builder.Append(MarkdownMath.Unicode(m.Tex)); break;
                case MarkdownInline.Emphasis e: builder.Append(e.Content.PlainText()); break;
                case MarkdownInline.Strong s: builder.Append(s.Content.PlainText()); break;
                case MarkdownInline.Strikethrough s: builder.Append(s.Content.PlainText()); break;
                case MarkdownInline.Link l: builder.Append(l.Content.PlainText()); break;
                case MarkdownInline.Image i: builder.Append(i.Alt.PlainText()); break;
                case MarkdownInline.Footnote: break;
                case MarkdownInline.LineBreak: builder.Append('\n'); break;
                case MarkdownInline.SoftBreak: builder.Append(' '); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>A paragraph made of one image (and nothing else) is shown as a figure.</summary>
    public MarkdownInline.Image? FigureImage() => Count == 1 ? this[0] as MarkdownInline.Image : null;
}

public abstract record MarkdownInline
{
    public sealed record Text(string Value) : MarkdownInline;
    public sealed record Emphasis(MarkdownText Content) : MarkdownInline;
    public sealed record Strong(MarkdownText Content) : MarkdownInline;
    public sealed record Strikethrough(MarkdownText Content) : MarkdownInline;
    public sealed record Code(string Value) : MarkdownInline;
    /// <summary>Inline math, as TeX source.</summary>
    public sealed record Math(string Tex) : MarkdownInline;
    public sealed record Link(string Url, string? Title, MarkdownText Content) : MarkdownInline;
    public sealed record Image(string Source, string? Title, MarkdownText Alt) : MarkdownInline;
    /// <summary>A footnote reference, numbered from 1.</summary>
    public sealed record Footnote(int Number) : MarkdownInline;
    public sealed record LineBreak : MarkdownInline;
    public sealed record SoftBreak : MarkdownInline;
}

public enum MarkdownAlignment { None, Left, Center, Right }

public sealed class MarkdownList<TText>
{
    public bool Ordered { get; set; }
    public int Start { get; set; } = 1;
    /// <summary>Items without blank lines between them, set close together.</summary>
    public bool Tight { get; set; } = true;
    public List<List<MarkdownBlock<TText>>> Items { get; set; } = [];
}

public sealed class MarkdownTable<TText>
{
    public List<TText>? Header { get; set; }
    public List<List<TText>> Rows { get; set; } = [];
    public List<MarkdownAlignment> Alignments { get; set; } = [];
    public TText? Caption { get; set; }
    /// <summary>Each column's share of the width, for multi-line tables; null to fit the contents.</summary>
    public List<double>? Widths { get; set; }
    public int ColumnCount => Alignments.Count;
}

public sealed class MarkdownDefinition<TText>
{
    public required TText Term { get; set; }
    public List<List<MarkdownBlock<TText>>> Definitions { get; set; } = [];
}

/// <summary>One block of a document. <typeparamref name="TText"/> is the raw source while blocks are being found, and inline
/// content once they're done.</summary>
public abstract record MarkdownBlock<TText>
{
    public sealed record Heading(int Level, TText Text, string Id) : MarkdownBlock<TText>;
    public sealed record Paragraph(TText Text) : MarkdownBlock<TText>;
    public sealed record Code(string Source, string? Language) : MarkdownBlock<TText>;
    /// <summary>Display math, as TeX source.</summary>
    public sealed record Math(string Tex) : MarkdownBlock<TText>;
    public sealed record Quote(List<MarkdownBlock<TText>> Blocks) : MarkdownBlock<TText>;
    public sealed record ListBlock(MarkdownList<TText> List) : MarkdownBlock<TText>;
    public sealed record Table(MarkdownTable<TText> Value) : MarkdownBlock<TText>;
    public sealed record Definitions(List<MarkdownDefinition<TText>> Items) : MarkdownBlock<TText>;
    /// <summary>A Pandoc line block: line breaks and leading spaces kept.</summary>
    public sealed record Lines(List<TText> Items) : MarkdownBlock<TText>;
    public sealed record Rule : MarkdownBlock<TText>;

    /// <summary>The same block with <paramref name="transform"/> applied to all its text, in document order.</summary>
    public MarkdownBlock<TOut> Map<TOut>(Func<TText, TOut> transform) => this switch
    {
        Heading h => new MarkdownBlock<TOut>.Heading(h.Level, transform(h.Text), h.Id),
        Paragraph p => new MarkdownBlock<TOut>.Paragraph(transform(p.Text)),
        Code c => new MarkdownBlock<TOut>.Code(c.Source, c.Language),
        Math m => new MarkdownBlock<TOut>.Math(m.Tex),
        Quote q => new MarkdownBlock<TOut>.Quote(q.Blocks.Select(b => b.Map(transform)).ToList()),
        ListBlock l => new MarkdownBlock<TOut>.ListBlock(new MarkdownList<TOut>
        {
            Ordered = l.List.Ordered, Start = l.List.Start, Tight = l.List.Tight,
            Items = l.List.Items.Select(item => item.Select(b => b.Map(transform)).ToList()).ToList(),
        }),
        Table t => new MarkdownBlock<TOut>.Table(new MarkdownTable<TOut>
        {
            Header = t.Value.Header?.Select(transform).ToList(),
            Rows = t.Value.Rows.Select(row => row.Select(transform).ToList()).ToList(),
            Alignments = t.Value.Alignments,
            Caption = t.Value.Caption is { } caption ? transform(caption) : default,
            Widths = t.Value.Widths,
        }),
        Definitions d => new MarkdownBlock<TOut>.Definitions(d.Items.Select(item => new MarkdownDefinition<TOut>
        {
            Term = transform(item.Term),
            Definitions = item.Definitions.Select(blocks => blocks.Select(b => b.Map(transform)).ToList()).ToList(),
        }).ToList()),
        Lines l => new MarkdownBlock<TOut>.Lines(l.Items.Select(transform).ToList()),
        Rule => new MarkdownBlock<TOut>.Rule(),
        _ => throw new InvalidOperationException(),
    };
}
