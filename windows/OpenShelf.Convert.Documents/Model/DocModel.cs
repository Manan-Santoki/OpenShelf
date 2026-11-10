using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>A run of text with simple inline styling. <see cref="Size"/> (points) is only set by readers that know it, to
/// recognise headings by their size (DocumentStructure.swift).</summary>
public sealed record TextRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false, string? Link = null, double? Size = null);

/// <summary>One block of a document: the structure model every text-document reader produces and every writer takes.</summary>
public abstract record Block;
public sealed record HeadingBlock(int Level, IReadOnlyList<TextRun> Runs) : Block;
public sealed record ParagraphBlock(IReadOnlyList<TextRun> Runs) : Block;
public sealed record ListItemBlock(bool Ordered, int Level, IReadOnlyList<TextRun> Runs) : Block;
/// <summary>A table; each cell is its text (lines separated by "\n").</summary>
public sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<TextRun>>> Rows, bool HasHeader) : Block;
/// <summary>A picture; <see cref="Extension"/> is png, jpeg or gif. Size in CSS pixels.</summary>
public sealed record ImageBlock(byte[] Data, string Extension, int Width, int Height, string Alt = "") : Block
{
    public string MediaType => "image/" + Extension;
}
public sealed record PageBreakBlock : Block;

public sealed record DocumentModel(string Title, IReadOnlyList<Block> Blocks)
{
    public static IReadOnlyList<TextRun> Plain(string text) => [new TextRun(text)];
}

/// <summary>Plain-text helpers shared by readers and writers.</summary>
public static class Texts
{
    public static string Of(IEnumerable<TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    public static IReadOnlyList<TextRun>? RunsOf(Block block) => block switch
    {
        HeadingBlock h => h.Runs,
        ParagraphBlock p => p.Runs,
        ListItemBlock l => l.Runs,
        _ => null,
    };

    /// <summary>Removes characters XML 1.0 cannot carry.</summary>
    public static string XmlSafe(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF && !char.IsSurrogate(c)) || char.IsSurrogate(c))
                builder.Append(c);
        return builder.ToString();
    }

    public static string Escape(string text)
    {
        var safe = XmlSafe(text);
        return safe.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}

/// <summary>Reads text files whatever their encoding (TextDecoding in DocumentConversion.swift).</summary>
public static class TextDecoding
{
    static TextDecoding() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string Decode(byte[] data)
    {
        if (data.Length == 0) return "";
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return Encoding.UTF8.GetString(data, 3, data.Length - 3);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        try { return new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { }
        // Windows-1252 maps nearly every byte, so it is a safe last resort.
        return Encoding.GetEncoding(1252).GetString(data);
    }

    public static string Read(string path) => Decode(File.ReadAllBytes(path));
}
