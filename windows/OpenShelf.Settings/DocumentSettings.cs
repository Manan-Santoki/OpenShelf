using OpenShelf.Core;
using System.Text.Json;

namespace OpenShelf.Settings;

/// <summary>How documents are converted. Kept under the preferences key "documents".</summary>
public sealed record DocumentSettings
{
    /// <summary>Let Word, Excel and PowerPoint convert their own files when installed: the most faithful PDFs.</summary>
    public bool UseMicrosoftOffice { get; init; } = true;

    /// <summary>Curl quotes and turn -- and ... into dashes and an ellipsis in Markdown. Off: text stays as typed.</summary>
    public bool SmartPunctuation { get; init; }

    public static DocumentSettings Default { get; } = new();

    public string Stored => JsonSerializer.Serialize(new { useMicrosoftOffice = UseMicrosoftOffice, smartPunctuation = SmartPunctuation });

    public static DocumentSettings Decode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            bool Read(string name, bool fallback) =>
                root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
            return new DocumentSettings
            {
                UseMicrosoftOffice = Read("useMicrosoftOffice", Default.UseMicrosoftOffice),
                SmartPunctuation = Read("smartPunctuation", Default.SmartPunctuation),
            };
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}

public static class DocumentPreferences
{
    public const string Key = "documents";

    public static DocumentSettings GetDocuments(this IPreferences preferences) => DocumentSettings.Decode(preferences.GetJson(Key));

    public static void SetDocuments(this IPreferences preferences, DocumentSettings settings) =>
        preferences.SetJson(Key, settings == DocumentSettings.Default ? null : settings.Stored);

    public const string CutoutKey = "highQualityCutouts";

    /// <summary>Remove BG with the large ISNet model (178 MB download, finer edges) instead of the small fast one. Default off.</summary>
    public static bool GetHighQualityCutouts(this IPreferences preferences) => preferences.GetJson(CutoutKey) == "true";

    public static void SetHighQualityCutouts(this IPreferences preferences, bool on) => preferences.SetJson(CutoutKey, on ? "true" : null);
}
