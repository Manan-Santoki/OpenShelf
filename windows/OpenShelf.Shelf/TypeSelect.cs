using System.Globalization;

namespace OpenShelf.Shelf;

/// <summary>Explorer/Finder-style type-to-select: characters typed in quick succession build a prefix, and the
/// first item whose name starts with it is selected (case- and accent-insensitive).</summary>
internal sealed class TypeSelect
{
    /// <summary>Typing after this long a pause starts a new prefix.</summary>
    public double TimeoutMs { get; init; } = 1000;

    public string Prefix { get; private set; } = "";
    double? _last;

    /// <summary>Adds typed characters and returns the prefix to match.</summary>
    public string Append(string characters, double nowMs)
    {
        if (_last is { } last && nowMs - last > TimeoutMs) Prefix = "";
        Prefix += characters;
        _last = nowMs;
        return Prefix;
    }

    public void Reset() { Prefix = ""; _last = null; }

    /// <summary>Index of the first name starting with <paramref name="prefix"/>, or null.</summary>
    public static int? Match(string prefix, IReadOnlyList<string> names)
    {
        if (string.IsNullOrEmpty(prefix)) return null;
        var compare = CultureInfo.InvariantCulture.CompareInfo;
        const CompareOptions options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
        for (int i = 0; i < names.Count; i++)
            if (compare.IsPrefix(names[i], prefix, options)) return i;
        return null;
    }

    /// <summary>Whether typed text should feed type-select (a letter, digit, punctuation or symbol; not space or control).</summary>
    public static bool IsTypingText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var info = new StringInfo(text);
        if (info.LengthInTextElements != 1) return false;
        var cat = CharUnicodeInfo.GetUnicodeCategory(text, 0);
        return cat switch
        {
            UnicodeCategory.Control or UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse => false,
            _ => true,
        };
    }
}
