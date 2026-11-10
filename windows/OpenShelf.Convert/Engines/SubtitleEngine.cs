using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenShelf.Convert;

/// <summary>SubRip (SRT) and WebVTT subtitles: convert between them or strip to plain text. Port of SubtitleConversion.swift.
/// Unlike the Mac, a file that is not UTF-8 (a BOM, UTF-16, Windows-1252 from Notepad or an old player) is still read.</summary>
public sealed class SubtitleEngine : IConversionEngine
{
    public sealed record Cue(string Start, string End, IReadOnlyList<string> Lines)
    {
        public bool Equals(Cue? other) => other is not null && Start == other.Start && End == other.End && Lines.SequenceEqual(other.Lines);
        public override int GetHashCode() => HashCode.Combine(Start, End, Lines.Count);
    }

    public bool CanConvert(FileKind kind, OutputFormat format) =>
        kind == FileKind.Subtitle && format is OutputFormat.Txt or OutputFormat.Srt or OutputFormat.Vtt;

    public Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var text = TextDecoding.Read(request.SourcePath) ?? throw ConversionException.Unreadable(request.SourcePath);
        var cues = Parse(text);
        var output = request.Format switch
        {
            OutputFormat.Txt => PlainText(cues),
            OutputFormat.Srt => Srt(cues),
            OutputFormat.Vtt => Vtt(cues),
            _ => throw ConversionException.Unsupported($"subtitles to {request.Format.Title()}"),
        };
        var destination = request.OutputPath($"{request.SourceStem}.{request.Format.FileExtension()}");
        File.WriteAllText(destination, output, new UTF8Encoding(false));
        progress?.Report(1);
        return Task.FromResult<IReadOnlyList<string>>([destination]);
    }

    /// <summary>Reads cues from either format; headers, notes and style blocks are skipped.</summary>
    public static IReadOnlyList<Cue> Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimStart('﻿');
        var cues = new List<Cue>();
        foreach (var block in normalized.Split("\n\n"))
        {
            var lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var timing = Array.FindIndex(lines, line => line.Contains("-->"));
            if (timing < 0) continue;
            var parts = lines[timing].Split("-->");
            if (parts.Length != 2) continue;
            var start = parts[0].Trim(' ', '\t');
            // VTT allows cue settings after the end time.
            var end = parts[1].Trim(' ', '\t').Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            cues.Add(new Cue(start, end, lines[(timing + 1)..]));
        }
        return cues;
    }

    public static string Srt(IReadOnlyList<Cue> cues) =>
        string.Join("\n\n", cues.Select((cue, index) =>
            $"{index + 1}\n{Timestamp(cue.Start, ",")} --> {Timestamp(cue.End, ",")}\n{string.Join('\n', cue.Lines)}")) + "\n";

    public static string Vtt(IReadOnlyList<Cue> cues) =>
        "WEBVTT\n\n" + string.Join("\n\n", cues.Select(cue =>
            $"{Timestamp(cue.Start, ".")} --> {Timestamp(cue.End, ".")}\n{string.Join('\n', cue.Lines)}")) + "\n";

    /// <summary>Dialogue only: no numbers, timings, or formatting tags.</summary>
    public static string PlainText(IReadOnlyList<Cue> cues) =>
        string.Join("\n", cues.SelectMany(cue => cue.Lines)
            .Select(line => Regex.Replace(line, @"<[^>]+>|\{[^}]+\}", ""))
            .Where(line => line.Trim(' ', '\t').Length > 0)) + "\n";

    /// <summary>Normalises "1:02.5" or "00:01:02,500" to "00:01:02&lt;sep&gt;500".</summary>
    public static string Timestamp(string value, string separator)
    {
        var components = value.Replace(',', '.').Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        while (components.Count < 3) components.Insert(0, "0");
        var secondsParts = components[2].Split('.');
        static int Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        var fraction = secondsParts.Length > 1 ? secondsParts[1] : "0";
        var milliseconds = Number((fraction + "000")[..3]);
        return string.Create(CultureInfo.InvariantCulture, $"{Number(components[0]):D2}:{Number(components[1]):D2}:{Number(secondsParts[0]):D2}{separator}{milliseconds:D3}");
    }
}
