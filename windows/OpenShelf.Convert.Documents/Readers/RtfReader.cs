using System.Globalization;
using System.Text;

namespace OpenShelf.Convert.Documents;

/// <summary>A small, tolerant RTF reader: text, bold, italic, underline, font sizes, paragraphs, line breaks, hyperlinks, simple
/// tables, Unicode and code-page escapes and embedded PNG/JPEG pictures. Anything richer is left to LibreOffice or Pandoc.</summary>
public static class RtfReader
{
    private sealed class Group
    {
        public bool Bold, Italic, Underline;
        public int FontSize;            // half-points, 0 = unknown
        public bool Skip;                // an ignored destination
        public int Uc = 1;
        public string? Link;
        public bool Picture;
        public string PictureKind = "";
        public int PictureWidth, PictureHeight;
        public bool FieldInstruction;
        public Group Clone() => (Group)MemberwiseClone();
    }

    private static readonly HashSet<string> IgnoredDestinations =
    [
        "fonttbl", "colortbl", "stylesheet", "info", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr", "footerf",
        "listtable", "listoverridetable", "revtbl", "generator", "filetbl", "rsidtbl", "latentstyles", "themedata", "colorschememapping",
        "datastore", "xmlnstbl", "pgptbl", "private", "mmathPr", "pnseclvl", "operator", "title", "author", "company", "footnote",
        "bkmkstart", "bkmkend", "xe", "tc", "nonshppict", "shpinst", "falt", "panose", "fldprop",
    ];

    public static DocumentModel Read(string path)
    {
        var file = path;
        if (Directory.Exists(path)) file = Path.Combine(path, "TXT.rtf");   // RTFD package
        byte[] data;
        try { data = File.ReadAllBytes(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw ConversionException.Unreadable(path); }
        var blocks = Parse(data, Directory.Exists(path) ? path : null);
        return new DocumentModel(Path.GetFileNameWithoutExtension(path), blocks);
    }

    public static IReadOnlyList<Block> Parse(string rtf) => Parse(Encoding.Latin1.GetBytes(rtf), null);

    public static IReadOnlyList<Block> Parse(byte[] data, string? packageFolder)
    {
        TextDecoding.Decode([]);   // registers the code-page encodings
        var text = Encoding.Latin1.GetString(data);
        if (!text.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal)) throw ConversionException.Failed("This isn’t an RTF document.");

        var codePage = 1252;
        var cpMatch = System.Text.RegularExpressions.Regex.Match(text, @"\\ansicpg(\d+)");
        if (cpMatch.Success) codePage = int.Parse(cpMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        Encoding encoding;
        try { encoding = Encoding.GetEncoding(codePage); } catch (ArgumentException) { encoding = Encoding.GetEncoding(1252); }

        var blocks = new List<Block>();
        var runs = new List<TextRun>();
        var stack = new Stack<Group>();
        var current = new Group();
        var bytes = new List<byte>();
        var skipChars = 0;
        var hex = new StringBuilder();
        var instruction = new StringBuilder();
        var tableRows = new List<IReadOnlyList<IReadOnlyList<TextRun>>>();
        var rowCells = new List<IReadOnlyList<TextRun>>();
        var inTable = false;

        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            AddText(encoding.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        void AddText(string value)
        {
            if (current.Skip || value.Length == 0) return;
            if (current.FieldInstruction) { instruction.Append(value); return; }
            if (current.Picture) return;
            runs.Add(new TextRun(value, current.Bold, current.Italic, current.Underline, current.Link, current.FontSize > 0 ? current.FontSize / 2.0 : null));
        }
        void FlushTable()
        {
            if (tableRows.Count == 0) return;
            blocks.Add(new TableBlock(tableRows.ToList(), false));
            tableRows.Clear();
        }
        void EndParagraph()
        {
            FlushBytes();
            if (inTable)
            {
                // Paragraphs inside a table cell are lines of the cell.
                if (runs.Count > 0) runs.Add(new TextRun("\n", Size: null));
                return;
            }
            FlushTable();
            var merged = Merge(runs);
            runs = [];
            if (merged.Count == 0) return;
            blocks.Add(new ParagraphBlock(merged));
        }
        void EndCell()
        {
            FlushBytes();
            var cell = Merge(runs);
            runs = [];
            while (cell.Count > 0 && cell[^1].Text.EndsWith('\n'))
            {
                var trimmed = cell[^1].Text.TrimEnd('\n');
                if (trimmed.Length == 0) cell.RemoveAt(cell.Count - 1); else cell[^1] = cell[^1] with { Text = trimmed };
            }
            rowCells.Add(cell.Select(run => run with { Size = null }).ToList());
        }
        void FinishPicture()
        {
            if (hex.Length == 0) return;
            try
            {
                var picture = System.Convert.FromHexString(hex.Length % 2 == 0 ? hex.ToString() : hex.ToString(0, hex.Length - 1));
                int? width = current.PictureWidth > 0 ? current.PictureWidth / 15 : null;      // twips to px
                int? height = current.PictureHeight > 0 ? current.PictureHeight / 15 : null;
                if (Pictures.Block(picture, "", width, height) is { } image)
                {
                    FlushBytes();
                    if (runs.Count > 0) { blocks.Add(new ParagraphBlock(Merge(runs))); runs = []; }
                    blocks.Add(image);
                }
            }
            catch (FormatException) { }
            hex.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '{':
                    FlushBytes();
                    stack.Push(current);
                    current = current.Clone();
                    current.Picture = false;
                    break;
                case '}':
                    FlushBytes();
                    if (current.Picture) FinishPicture();
                    var closedInstruction = current.FieldInstruction;
                    if (stack.Count > 0) current = stack.Pop();
                    // The instruction group is over: its hyperlink applies to the result that follows in the field.
                    if (closedInstruction && !current.FieldInstruction)
                    {
                        if (HyperlinkIn(instruction.ToString()) is { } link) current.Link = link;
                        instruction.Clear();
                    }
                    break;
                case '\\':
                    i++;
                    if (i >= text.Length) break;
                    var next = text[i];
                    if (char.IsAsciiLetter(next))
                    {
                        var start = i;
                        while (i < text.Length && char.IsAsciiLetter(text[i])) i++;
                        var word = text[start..i];
                        int? parameter = null;
                        var negative = false;
                        if (i < text.Length && text[i] == '-') { negative = true; i++; }
                        var digits = i;
                        while (i < text.Length && char.IsAsciiDigit(text[i])) i++;
                        if (i > digits && int.TryParse(text.AsSpan(digits, i - digits), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                            parameter = negative ? -number : number;
                        // One space ends a control word and belongs to it.
                        if (i < text.Length && text[i] == ' ') i++;
                        i--;
                        Control(word, parameter);
                    }
                    else
                    {
                        switch (next)
                        {
                            case '\\' or '{' or '}':
                                if (skipChars > 0) skipChars--; else { FlushBytes(); AddText(next.ToString()); }
                                break;
                            case '\'':
                                if (i + 2 < text.Length + 0 && byte.TryParse(text.AsSpan(i + 1, Math.Min(2, text.Length - i - 1)), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                                {
                                    i += 2;
                                    if (skipChars > 0) skipChars--; else if (!current.Skip) bytes.Add(value);
                                }
                                break;
                            case '~': FlushBytes(); AddText("\u00A0"); break;
                            case '_': FlushBytes(); AddText("\u2011"); break;
                            case '-': break;
                            case '*':
                                // An unknown destination: skip it unless it is the field instruction.
                                var rest = text.AsSpan(i + 1);
                                var trimmed = rest.TrimStart();
                                var offset = rest.Length - trimmed.Length;
                                if (trimmed.StartsWith("\\fldinst")) { current.FieldInstruction = true; i += offset + "\\fldinst".Length; }
                                else if (!trimmed.StartsWith("\\listtext") && !trimmed.StartsWith("\\pn") && !trimmed.StartsWith("\\shppict")) current.Skip = true;
                                break;
                            case '\r' or '\n':
                                EndParagraph();
                                break;
                        }
                    }
                    break;
                case '\r' or '\n':
                    break;
                default:
                    if (current.Picture) { if (Uri.IsHexDigit(c)) hex.Append(c); break; }
                    if (skipChars > 0) { skipChars--; break; }
                    if (current.Skip) break;
                    FlushBytes();
                    // Gather a plain stretch in one go.
                    var end = i;
                    while (end + 1 < text.Length && text[end + 1] is not ('\\' or '{' or '}' or '\r' or '\n')) end++;
                    AddText(Encoding.Latin1.GetString(Encoding.Latin1.GetBytes(text[i..(end + 1)])));
                    i = end;
                    break;
            }
        }
        FlushBytes();
        if (runs.Count > 0) { var merged = Merge(runs); if (merged.Count > 0) blocks.Add(new ParagraphBlock(merged)); }
        FlushTable();
        return DocumentStructure.Normalize(blocks);

        void Control(string word, int? parameter)
        {
            if (IgnoredDestinations.Contains(word) && !current.Skip)
            {
                current.Skip = true;
                return;
            }
            switch (word)
            {
                case "b": current.Bold = parameter != 0; break;
                case "i": current.Italic = parameter != 0; break;
                case "ul" or "uldb" or "ulw": current.Underline = parameter != 0; break;
                case "ulnone": current.Underline = false; break;
                case "fs": current.FontSize = parameter ?? 0; break;
                case "plain": current.Bold = current.Italic = current.Underline = false; current.FontSize = 0; break;
                case "uc": current.Uc = parameter ?? 1; break;
                case "u":
                    if (parameter is { } code)
                    {
                        FlushBytes();
                        AddText(((char)(code < 0 ? code + 65536 : code)).ToString());
                        skipChars = current.Uc;
                    }
                    break;
                case "par" or "sect" or "pagebb": if (!current.Skip) EndParagraph(); break;
                case "line": FlushBytes(); AddText("\n"); break;
                case "tab": FlushBytes(); AddText("\t"); break;
                case "emdash": FlushBytes(); AddText("—"); break;
                case "endash": FlushBytes(); AddText("–"); break;
                case "bullet": FlushBytes(); AddText("•"); break;
                case "lquote": FlushBytes(); AddText("‘"); break;
                case "rquote": FlushBytes(); AddText("’"); break;
                case "ldblquote": FlushBytes(); AddText("“"); break;
                case "rdblquote": FlushBytes(); AddText("”"); break;
                case "emspace" or "enspace": FlushBytes(); AddText(" "); break;
                case "trowd": FlushBytes(); if (!inTable) { if (runs.Count > 0) { var before = Merge(runs); runs = []; if (before.Count > 0) blocks.Add(new ParagraphBlock(before)); } } inTable = true; rowCells = []; break;
                case "intbl": inTable = true; break;
                case "cell": if (!current.Skip) EndCell(); break;
                case "row":
                    if (!current.Skip)
                    {
                        if (runs.Count > 0) EndCell();
                        if (rowCells.Count > 0) tableRows.Add(rowCells);
                        rowCells = [];
                        inTable = false;
                    }
                    break;
                case "pard": if (inTable && tableRows.Count == 0 && rowCells.Count == 0 && runs.Count == 0) inTable = false; break;
                case "pict": current.Picture = true; hex.Clear(); break;
                case "pngblip": current.PictureKind = "png"; break;
                case "jpegblip": current.PictureKind = "jpeg"; break;
                case "picwgoal": current.PictureWidth = parameter ?? 0; break;
                case "pichgoal": current.PictureHeight = parameter ?? 0; break;
                case "fldinst": current.FieldInstruction = true; break;
                case "fldrslt": break;
            }
        }
    }

    private static string? HyperlinkIn(string instruction)
    {
        var trimmed = instruction.Trim();
        if (!trimmed.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
        var start = trimmed.IndexOf('"');
        var end = start >= 0 ? trimmed.IndexOf('"', start + 1) : -1;
        return start >= 0 && end > start ? trimmed[(start + 1)..end] : null;
    }

    private static List<TextRun> Merge(List<TextRun> runs)
    {
        var merged = new List<TextRun>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1] is { } last && last.Bold == run.Bold && last.Italic == run.Italic && last.Underline == run.Underline && last.Link == run.Link && last.Size == run.Size)
                merged[^1] = last with { Text = last.Text + run.Text };
            else merged.Add(run);
        }
        // Whitespace-only paragraphs are empty.
        return merged.Count == 1 && string.IsNullOrWhiteSpace(merged[0].Text) ? [] : merged;
    }
}
