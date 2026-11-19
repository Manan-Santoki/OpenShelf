using Microsoft.Win32;

namespace OpenShelf.Convert;

/// <summary>The Microsoft Office desktop apps OpenShelf can convert with.</summary>
public enum OfficeApp { Word, Excel, PowerPoint }

/// <summary>Whether an Office desktop app is registered for automation: a registry read, no Office process is started.</summary>
public static class OfficeInstallation
{
    private static readonly Dictionary<OfficeApp, bool> Cache = [];

    public static IReadOnlyList<OfficeApp> All { get; } = [OfficeApp.Word, OfficeApp.Excel, OfficeApp.PowerPoint];

    /// <summary>Settings → Output → "Convert with Microsoft Office". When off, Office is treated as not installed.</summary>
    public static bool Enabled { get; set; } = true;

    public static string ProgId(OfficeApp app) => app switch
    {
        OfficeApp.Word => "Word.Application",
        OfficeApp.Excel => "Excel.Application",
        _ => "PowerPoint.Application",
    };

    public static bool IsInstalled(OfficeApp app)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(app, out var known)) return known;
            var installed = false;
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(ProgId(app) + @"\CLSID");
                installed = key?.GetValue(null) is string clsid && clsid.Length > 0 && Type.GetTypeFromProgID(ProgId(app)) is not null;
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            Cache[app] = installed;
            return installed;
        }
    }

    /// <summary>Forgets what was found, after Office is installed or removed.</summary>
    public static void Refresh()
    {
        lock (Cache) Cache.Clear();
    }
}

/// <summary>The extensions each Office app opens for OpenShelf.</summary>
public static class OfficeSources
{
    public static readonly IReadOnlySet<string> Word =
        new HashSet<string> { "docx", "docm", "doc", "dotx", "dotm", "dot", "rtf", "odt" };
    public static readonly IReadOnlySet<string> Excel =
        new HashSet<string> { "xlsx", "xlsm", "xlsb", "xls", "ods" };
    public static readonly IReadOnlySet<string> PowerPoint =
        new HashSet<string> { "pptx", "pptm", "ppt", "ppsx", "pps", "odp" };

    public static OfficeApp? AppFor(string extension) =>
        Word.Contains(extension) ? OfficeApp.Word :
        Excel.Contains(extension) ? OfficeApp.Excel :
        PowerPoint.Contains(extension) ? OfficeApp.PowerPoint : null;
}
