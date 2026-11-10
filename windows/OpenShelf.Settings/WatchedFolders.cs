using System.Text;
using System.Text.Json;

namespace OpenShelf.Settings;

/// <summary>One watched folder. <paramref name="Action"/> is null (just add to a shelf) or an entry id such as "convert.jpg".</summary>
public sealed record WatchedFolder(string Path, bool Enabled = true, string? Action = null);

/// <summary>The watched-folder list as stored under the preferences key "watchedFolders":
/// an array of { "path", "enabled", "action" }. Unreadable entries are skipped one by one.</summary>
public static class WatchedFolderList
{
    public const string Key = "watchedFolders";

    public static IReadOnlyList<WatchedFolder> Decode(string? json)
    {
        var list = new List<WatchedFolder>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object
                    || !e.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(p.GetString())) continue;
                var enabled = !(e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False);
                var action = e.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() is { Length: > 0 } s ? s : null;
                var path = p.GetString()!;
                if (list.Any(f => SamePath(f.Path, path))) continue;
                list.Add(new WatchedFolder(path, enabled, action));
            }
        }
        catch (JsonException) { }
        return list;
    }

    public static string Encode(IEnumerable<WatchedFolder> folders)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var f in folders)
            {
                w.WriteStartObject();
                w.WriteString("path", f.Path);
                w.WriteBoolean("enabled", f.Enabled);
                if (f.Action is null) w.WriteNull("action"); else w.WriteString("action", f.Action);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Where Windows saves Win+PrtScn screenshots.</summary>
    public static string ScreenshotsFolder =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>The actions offered for new files, in menu order: (id, title).</summary>
    public static readonly IReadOnlyList<(string? Id, string Title)> Actions =
    [
        (null, "Only add to shelf"),
        ("convert.jpg", "Convert to JPG"), ("convert.png", "Convert to PNG"), ("convert.heic", "Convert to HEIC"),
        ("convert.webp", "Convert to WebP"), ("convert.avif", "Convert to AVIF"), ("convert.pdf", "Convert to PDF"),
        ("convert.mp4", "Convert to MP4"), ("convert.gif", "Convert to GIF"),
        ("convert.m4a", "Convert to M4A"), ("convert.mp3", "Convert to MP3"),
        ("tool.compress", "Compress"), ("tool.removebg", "Remove Background"), ("tool.ocr", "OCR"),
        ("tool.readqr", "Read QR"), ("tool.mute", "Mute"),
    ];
}
