using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace OpenShelf.Convert;

/// <summary>Comma- and tab-separated text (RFC 4180 quoting). Port of DelimitedText in Spreadsheet.swift. A table is a list of rows of cell texts.</summary>
public static class DelimitedText
{
    /// <summary>The number of records in a CSV or TSV file, read in chunks without building the table: line breaks inside
    /// quoted fields do not count. Follows the same rules as <see cref="Parse"/>.</summary>
    public static int RecordCount(string path, char delimiter = ',', CancellationToken ct = default)
    {
        var count = 0;
        var inQuotes = false;
        var pendingQuote = false;
        var fieldHasContent = false;
        var hasFields = false;
        var afterCR = false;

        void Consume(int unit)
        {
            if (afterCR)
            {
                afterCR = false;
                if (unit == 0x0A) return;
            }
            if (inQuotes)
            {
                if (unit == 0x22)
                {
                    if (pendingQuote) { fieldHasContent = true; pendingQuote = false; }
                    else pendingQuote = true;
                    return;
                }
                if (pendingQuote) { inQuotes = false; pendingQuote = false; }
                else { fieldHasContent = true; return; }
            }
            if (unit == 0x22 && !fieldHasContent) inQuotes = true;
            else if (unit == delimiter) { hasFields = true; fieldHasContent = false; }
            else if (unit == 0x0A || unit == 0x0D)
            {
                count++;
                fieldHasContent = false;
                hasFields = false;
                afterCR = unit == 0x0D;
            }
            else fieldHasContent = true;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[1 << 20];
        var first = true;
        bool? utf16LittleEndian = null;
        int? pendingByte = null;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var start = 0;
            if (first)
            {
                first = false;
                if (read >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE) { utf16LittleEndian = true; start = 2; }
                else if (read >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF) { utf16LittleEndian = false; start = 2; }
                else if (read >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF) start = 3;
            }
            for (var i = start; i < read; i++)
            {
                var value = buffer[i];
                if (utf16LittleEndian is { } little)
                {
                    if (pendingByte is { } leading)
                    {
                        Consume(little ? leading | value << 8 : leading << 8 | value);
                        pendingByte = null;
                    }
                    else pendingByte = value;
                }
                else Consume(value);
            }
        }
        return count + (fieldHasContent || hasFields ? 1 : 0);
    }

    public static List<List<string>> Parse(string text, char delimiter)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        void EndField() { row.Add(field.ToString()); field.Clear(); }
        void EndRow() { EndField(); rows.Add(row); row = []; }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    // A doubled quote is a literal quote; a single one closes the field.
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"' && field.Length == 0) inQuotes = true;
            else if (c == delimiter) EndField();
            else if (c == '\n') EndRow();
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                EndRow();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) EndRow();
        return rows;
    }

    public static string Write(IReadOnlyList<IReadOnlyList<string>> table, char delimiter)
    {
        var builder = new StringBuilder();
        foreach (var row in table)
        {
            builder.AppendJoin(delimiter, row.Select(field =>
            {
                var needsQuotes = field.Contains(delimiter) || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
                return needsQuotes ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
            }));
            builder.Append('\n');
        }
        return builder.ToString();
    }

    public static List<List<string>> Read(string path, char delimiter)
    {
        var text = TextDecoding.Read(path) ?? throw ConversionException.Unreadable(path);
        return Parse(text, delimiter);
    }
}

/// <summary>Excel opens at most 1,048,576 rows a sheet, the header row included, so larger tables are refused before the
/// (slow, memory-hungry) conversion.</summary>
public static class ExcelLimit
{
    public const int Rows = 1_048_576;

    public static void Check(int rows, int limit = Rows)
    {
        if (rows <= limit) return;
        throw ConversionException.Failed($"Excel holds up to {limit:N0} rows; this has {rows:N0}. Save as CSV instead.");
    }
}

/// <summary>Writes a minimal Excel workbook (SpreadsheetML) with inline strings and numbers.</summary>
public static class XlsxWriter
{
    private const string RelationshipType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string XmlHeader = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";

    public static void Write(IReadOnlyList<(string Name, IReadOnlyList<IReadOnlyList<string>> Table)> sheets, string path, int rowLimit = ExcelLimit.Rows, CancellationToken ct = default)
    {
        foreach (var sheet in sheets) ExcelLimit.Check(sheet.Table.Count, rowLimit);
        if (sheets.Count == 0) sheets = [("Sheet1", [])];

        var sheetEntries = new StringBuilder();
        var sheetRelationships = new StringBuilder();
        var overrides = new StringBuilder();
        var used = new HashSet<string>();

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        for (var index = 0; index < sheets.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var number = index + 1;
            var name = UniqueSheetName(sheets[index].Name, used);
            var entry = zip.CreateEntry($"xl/worksheets/sheet{number}.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) WriteWorksheet(writer, sheets[index].Table);
            sheetEntries.Append($"<sheet name=\"{XmlEscape(name)}\" sheetId=\"{number}\" r:id=\"rId{number}\"/>");
            sheetRelationships.Append($"<Relationship Id=\"rId{number}\" Type=\"{RelationshipType}/worksheet\" Target=\"worksheets/sheet{number}.xml\"/>");
            overrides.Append($"<Override PartName=\"/xl/worksheets/sheet{number}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }
        sheetRelationships.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"{RelationshipType}/styles\" Target=\"styles.xml\"/>");

        Add(zip, "[Content_Types].xml", XmlHeader + $"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>{overrides}</Types>");
        Add(zip, "_rels/.rels", XmlHeader + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"{RelationshipType}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(zip, "xl/workbook.xml", XmlHeader + $"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"{RelationshipType}\"><sheets>{sheetEntries}</sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels", XmlHeader + $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">{sheetRelationships}</Relationships>");
        Add(zip, "xl/styles.xml", XmlHeader + Styles);
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(new UTF8Encoding(false).GetBytes(content));
    }

    private static void WriteWorksheet(StreamWriter writer, IReadOnlyList<IReadOnlyList<string>> table)
    {
        writer.Write(XmlHeader + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < table.Count; r++)
        {
            writer.Write($"<row r=\"{r + 1}\">");
            var row = table[r];
            for (var c = 0; c < row.Count; c++)
            {
                var value = row[c];
                if (value.Length == 0) continue;
                var reference = XlsxReader.ColumnName(c) + (r + 1);
                if (NumericValue(value) is { } number) writer.Write($"<c r=\"{reference}\"><v>{number}</v></c>");
                else writer.Write($"<c r=\"{reference}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{XmlEscape(value)}</t></is></c>");
            }
            writer.Write("</row>");
        }
        writer.Write("</sheetData></worksheet>");
    }

    /// <summary>The cell's text as a number when it reads as one and would survive the round trip unchanged (so "007" and "1e5" stay text).</summary>
    public static string? NumericValue(string text) =>
        Regex.IsMatch(text, @"^-?(0|[1-9][0-9]{0,14})(\.[0-9]{1,10})?$") && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? text : null;

    /// <summary>XML text with characters XML 1.0 cannot hold removed.</summary>
    public static string XmlEscape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c < 0x20 && c != '\t' && c != '\n' && c != '\r') continue;
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
    }

    /// <summary>Excel sheet names: at most 31 characters, none of []:*?/\, unique.</summary>
    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var cleaned = new string(name.Where(c => !"[]:*?/\\".Contains(c)).Take(31).ToArray());
        if (cleaned.Length == 0) cleaned = "Sheet";
        var candidate = cleaned;
        var counter = 2;
        while (used.Contains(candidate.ToLowerInvariant()))
        {
            var suffix = $" {counter}";
            candidate = cleaned[..Math.Min(cleaned.Length, 31 - suffix.Length)] + suffix;
            counter++;
        }
        used.Add(candidate.ToLowerInvariant());
        return candidate;
    }

    private const string Styles = "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
}

/// <summary>Reads the first worksheet of an Excel (.xlsx) workbook as text.</summary>
public static class XlsxReader
{
    public static List<List<string>> FirstSheet(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var workbook = zip.GetEntry("xl/workbook.xml") ?? throw ConversionException.Unreadable(path);
            var sheetPath = FirstSheetPath(workbook, zip.GetEntry("xl/_rels/workbook.xml.rels")) ?? "xl/worksheets/sheet1.xml";
            var sheet = zip.GetEntry(sheetPath) ?? throw ConversionException.Unreadable(path);
            var shared = zip.GetEntry("xl/sharedStrings.xml") is { } sharedEntry ? SharedStrings(sharedEntry) : [];
            using var stream = sheet.Open();
            return Cells(stream, shared);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException)
        {
            throw ConversionException.Unreadable(path);
        }
    }

    /// <summary>"A", "B", ... "Z", "AA", ... for a zero-based column.</summary>
    public static string ColumnName(int index)
    {
        index++;
        var name = "";
        while (index > 0)
        {
            var remainder = (index - 1) % 26;
            name = (char)('A' + remainder) + name;
            index = (index - 1) / 26;
        }
        return name;
    }

    /// <summary>Zero-based column of a cell reference like "AB12".</summary>
    public static int ColumnIndex(string reference)
    {
        var index = 0;
        foreach (var c in reference)
        {
            if (c < 'A' || c > 'Z') break;
            index = index * 26 + (c - 'A' + 1);
        }
        return Math.Max(index - 1, 0);
    }

    private static string? FirstSheetPath(ZipArchiveEntry workbook, ZipArchiveEntry? relationships)
    {
        if (relationships is null) return null;
        string? id = null;
        using (var reader = XmlReader.Create(workbook.Open()))
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet")
                {
                    id = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? reader.GetAttribute("r:id");
                    break;
                }
        if (id is null) return null;
        using var rels = XmlReader.Create(relationships.Open());
        while (rels.Read())
            if (rels.NodeType == XmlNodeType.Element && rels.LocalName == "Relationship" && rels.GetAttribute("Id") == id)
            {
                var target = rels.GetAttribute("Target");
                if (target is null) return null;
                return target.StartsWith('/') ? target[1..] : "xl/" + target;
            }
        return null;
    }

    /// <summary>The items of sharedStrings.xml, joining rich-text runs and leaving out phonetic hints.</summary>
    private static List<string> SharedStrings(ZipArchiveEntry entry)
    {
        var strings = new List<string>();
        using var reader = XmlReader.Create(entry.Open());
        StringBuilder? current = null;
        var inText = false;
        var phonetic = 0;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "si": current = new StringBuilder(); if (reader.IsEmptyElement) { strings.Add(""); current = null; } break;
                    case "rPh": phonetic++; break;
                    case "t": inText = !reader.IsEmptyElement; break;
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                if (inText && phonetic == 0) current?.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "t": inText = false; break;
                    case "rPh": phonetic--; break;
                    case "si": strings.Add(current?.ToString() ?? ""); current = null; break;
                }
            }
        }
        return strings;
    }

    /// <summary>Cell values of a worksheet as a rectangular table (sparse cells are empty strings).</summary>
    public static List<List<string>> Cells(Stream sheet, IReadOnlyList<string> sharedStrings)
    {
        var rows = new SortedDictionary<int, Dictionary<int, string>>();
        using var reader = XmlReader.Create(sheet);
        var rowIndex = 0;
        var column = 0;
        var cellType = "";
        var value = new StringBuilder();
        var collecting = false;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        rowIndex = (int.TryParse(reader.GetAttribute("r"), out var r) ? r : rowIndex + 1) - 1;
                        column = 0;
                        break;
                    case "c":
                        if (reader.GetAttribute("r") is { } reference) column = ColumnIndex(reference);
                        cellType = reader.GetAttribute("t") ?? "n";
                        value.Clear();
                        break;
                    case "v" or "t":
                        collecting = !reader.IsEmptyElement;
                        break;
                }
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace or XmlNodeType.Whitespace)
            {
                if (collecting) value.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "v" or "t":
                        collecting = false;
                        break;
                    case "c":
                        var raw = value.ToString();
                        var text = cellType switch
                        {
                            "s" => int.TryParse(raw, out var i) && i >= 0 && i < sharedStrings.Count ? sharedStrings[i] : "",
                            "b" => raw == "1" ? "TRUE" : "FALSE",
                            _ => raw,
                        };
                        if (text.Length > 0)
                        {
                            if (!rows.TryGetValue(rowIndex, out var cells)) rows[rowIndex] = cells = [];
                            cells[column] = text;
                        }
                        column++;
                        break;
                }
            }
        }
        if (rows.Count == 0) return [];
        var width = rows.Values.Select(cells => cells.Count == 0 ? 0 : cells.Keys.Max() + 1).Max();
        var last = rows.Keys.Max();
        var table = new List<List<string>>(last + 1);
        for (var row = 0; row <= last; row++)
        {
            rows.TryGetValue(row, out var cells);
            table.Add(Enumerable.Range(0, width).Select(col => cells is not null && cells.TryGetValue(col, out var text) ? text : "").ToList());
        }
        return table;
    }
}

/// <summary>CSV, TSV and XLSX to each other. ODS and XLS need LibreOffice, and the table PDF comes with the PDF engine; both are
/// separate engines registered ahead of this one.</summary>
public sealed class SpreadsheetEngine : IConversionEngine
{
    private static readonly string[] BuiltInExtensions = ["csv", "tsv", "xlsx"];

    public bool CanConvert(FileKind kind, OutputFormat format) =>
        kind == FileKind.Spreadsheet && format is OutputFormat.Xlsx or OutputFormat.Csv or OutputFormat.Tsv;

    public bool CanConvert(ConversionRequest request) =>
        CanConvert(FileKind.Spreadsheet, request.Format) && BuiltInExtensions.Contains(FileKinds.ExtensionOf(request.SourcePath));

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken) =>
        Task.Run(() => Convert(request, progress, cancellationToken, ExcelLimit.Rows), cancellationToken);

    /// <summary>Converts with a smaller row limit; for tests.</summary>
    public Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, int rowLimit, CancellationToken cancellationToken = default) =>
        Task.Run(() => Convert(request, null, cancellationToken, rowLimit), cancellationToken);

    private static IReadOnlyList<string> Convert(ConversionRequest request, IProgress<double>? progress, CancellationToken ct, int rowLimit)
    {
        var source = request.SourcePath;
        var ext = FileKinds.ExtensionOf(source);
        if (request.Format == OutputFormat.Xlsx && ext is "csv" or "tsv")
            ExcelLimit.Check(DelimitedText.RecordCount(source, ext == "tsv" ? '\t' : ',', ct), rowLimit);
        var table = Read(source);
        ct.ThrowIfCancellationRequested();
        var destination = request.OutputPath($"{request.SourceStem}.{request.Format.FileExtension()}");
        try
        {
            switch (request.Format)
            {
                case OutputFormat.Csv: File.WriteAllText(destination, DelimitedText.Write(table, ','), new UTF8Encoding(false)); break;
                case OutputFormat.Tsv: File.WriteAllText(destination, DelimitedText.Write(table, '\t'), new UTF8Encoding(false)); break;
                case OutputFormat.Xlsx: XlsxWriter.Write([(request.SourceStem, table.Select(r => (IReadOnlyList<string>)r).ToList())], destination, rowLimit, ct); break;
                default: throw ConversionException.Unsupported($"spreadsheet to {request.Format.Title()}");
            }
        }
        catch
        {
            try { File.Delete(destination); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        progress?.Report(1);
        return [destination];
    }

    public static List<List<string>> Read(string path) => FileKinds.ExtensionOf(path) switch
    {
        "csv" => DelimitedText.Read(path, ','),
        "tsv" => DelimitedText.Read(path, '\t'),
        "xlsx" => XlsxReader.FirstSheet(path),
        var other => throw ConversionException.Unsupported($"reading {other.ToUpperInvariant()}"),
    };
}
