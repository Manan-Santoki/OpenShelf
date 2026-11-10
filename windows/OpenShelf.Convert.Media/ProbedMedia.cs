using System.Globalization;
using System.Text.Json;

namespace OpenShelf.Convert;

/// <summary>A file's streams as ffprobe reports them. Port of ProbedMedia.swift, plus colour primaries and the container's rate.</summary>
public sealed record ProbedMedia(double Duration, ProbedVideo? Video, IReadOnlyList<ProbedAudio> Audio)
{
    /// <summary>The container's overall bit rate, when it says.</summary>
    public int? FormatBitRate { get; init; }

    public bool HasAudio => Audio.Count > 0;

    /// <summary>Parses ffprobe's <c>-of json</c> output; null for anything that is not JSON or holds no usable stream.</summary>
    public static ProbedMedia? Parse(string json)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            JsonElement? format = root.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Object ? f : null;
            int? formatBitRate = Number(format, "bit_rate") is { } fbr ? (int)fbr : null;

            ProbedVideo? video = null;
            var audio = new List<ProbedAudio>();
            var hasAnyVideoStream = false;
            var longest = 0.0;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    longest = Math.Max(longest, Number(stream, "duration") ?? 0);
                    if (Text(stream, "codec_type") == "video") hasAnyVideoStream = true;
                }
                foreach (var stream in streams.EnumerateArray())
                {
                    switch (Text(stream, "codec_type"))
                    {
                        case "video":
                            if (video is not null || IsAttachedPicture(stream)) break;
                            if (Number(stream, "width") is not { } width || Number(stream, "height") is not { } height) break;
                            video = new ProbedVideo((int)width, (int)height, Rotation(stream), FrameRate(stream),
                                Number(stream, "bit_rate") is { } vbr ? (int)vbr : null, Text(stream, "codec_name") ?? "",
                                Text(stream, "pix_fmt"), Text(stream, "color_transfer"), Text(stream, "color_primaries"));
                            break;
                        case "audio":
                            audio.Add(new ProbedAudio(Text(stream, "codec_name") ?? "", Math.Max((int)(Number(stream, "channels") ?? 2), 1),
                                Number(stream, "sample_rate"), Number(stream, "bit_rate") is { } abr ? (int)abr : null));
                            break;
                    }
                }
            }
            // For audio-only files whose stream has no rate (Ogg, Matroska) the container's overall rate stands in.
            if (!hasAnyVideoStream && formatBitRate is > 0)
                audio = audio.Select(a => a.BitRate is > 0 ? a : a with { BitRate = formatBitRate }).ToList();
            if (video is null && audio.Count == 0) return null;
            var duration = Number(format, "duration") ?? longest;
            return new ProbedMedia(duration, video, audio) { FormatBitRate = formatBitRate };
        }
    }

    private static bool IsAttachedPicture(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object
        && d.TryGetProperty("attached_pic", out var a) && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;

    private static string? FrameRate(JsonElement stream) =>
        new[] { Text(stream, "avg_frame_rate"), Text(stream, "r_frame_rate") }.FirstOrDefault(r => r is not null && (Rate(r) ?? 0) > 0);

    /// <summary>Clockwise degrees from the display matrix (counter-clockwise in ffprobe, so -90 for a portrait phone video),
    /// or from the older "rotate" tag (clockwise).</summary>
    private static int Rotation(JsonElement stream)
    {
        double clockwise = 0;
        var found = false;
        if (stream.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in sideData.EnumerateArray())
            {
                if (Number(entry, "rotation") is { } counterClockwise)
                {
                    clockwise = -Math.Round(counterClockwise);
                    found = true;
                    break;
                }
            }
        }
        if (!found && stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object && Number(tags, "rotate") is { } tag)
            clockwise = Math.Round(tag);
        var normalized = (((int)clockwise % 360) + 360) % 360;
        return normalized / 90 * 90;
    }

    /// <summary>"30000/1001" or "25" as a number; null for "0/0".</summary>
    public static double? Rate(string text)
    {
        var parts = text.Split('/');
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)) return null;
        var denominator = 1.0;
        if (parts.Length > 1 && !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator)) denominator = 0;
        return denominator > 0 && numerator > 0 ? numerator / denominator : null;
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>ffprobe writes most numbers as strings ("96000"), some as numbers.</summary>
    private static double? Number(JsonElement? element, string name)
    {
        if (element is not { ValueKind: JsonValueKind.Object } e || !e.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
    }
}

/// <summary>The first video stream that is not cover art.</summary>
/// <param name="Width">Frame size as stored, before rotation.</param>
/// <param name="Height">Frame height as stored.</param>
/// <param name="Rotation">Degrees players turn the picture clockwise: 0, 90, 180 or 270.</param>
/// <param name="FrameRate">ffmpeg's exact fraction ("30000/1001"), or null when unknown.</param>
/// <param name="BitRate">Bits per second; null when the container does not say.</param>
/// <param name="Codec">ffprobe's codec_name.</param>
/// <param name="PixelFormat">For example yuv420p.</param>
/// <param name="ColorTransfer">"smpte2084" (PQ) or "arib-std-b67" (HLG) for HDR.</param>
/// <param name="ColorPrimaries">For example bt2020.</param>
public sealed record ProbedVideo(int Width, int Height, int Rotation, string? FrameRate, int? BitRate, string Codec, string? PixelFormat, string? ColorTransfer, string? ColorPrimaries = null)
{
    /// <summary>The size players show, after rotation.</summary>
    public (int Width, int Height) DisplaySize => Rotation % 180 == 0 ? (Width, Height) : (Height, Width);

    public double? FramesPerSecond => FrameRate is null ? null : ProbedMedia.Rate(FrameRate);

    /// <summary>PQ ("smpte2084") or HLG ("arib-std-b67") transfer.</summary>
    public bool IsHdr => ColorTransfer is "smpte2084" or "arib-std-b67";
}

/// <summary>One sound stream.</summary>
public sealed record ProbedAudio(string Codec, int Channels, double? SampleRate, int? BitRate)
{
    /// <summary>What <see cref="AudioBitRate"/> needs to pick a rate for a lossy copy.</summary>
    public SourceAudio Source => new(Channels, BitRate, AudioBitRate.CodecFromFfprobe(Codec));
}
