using Microsoft.Win32;

namespace OpenShelf.Automation;

/// <summary>
/// The Explorer right-click entry "Open with OpenShelf", registered per user under
/// HKCU\Software\Classes\*\shell\OpenShelf (no admin rights). It is a classic verb, so on Windows 11 it sits under
/// "Show more options". The verb runs <c>"OpenShelf.exe" wheel "%1"</c>; with several files selected Explorer starts
/// one process per file (MultiSelectModel=Document), and the running app merges them with <see cref="CommandCoalescer"/>.
/// Explorer hides a classic verb when more than 15 files are selected.
/// </summary>
public static class ExplorerIntegration
{
    public const string VerbName = "OpenShelf";
    public const string MenuText = "Open with OpenShelf";
    /// <summary>The registry location of the per-user classes hive; tests pass a throwaway root instead.</summary>
    public const string DefaultClassesRoot = @"Software\Classes";

    private static string VerbPath(string classesRoot) => classesRoot.TrimEnd('\\') + @"\*\shell\" + VerbName;

    /// <summary>The command stored in the registry for an exe path.</summary>
    public static string CommandFor(string exePath) => $"\"{exePath}\" wheel \"%1\"";

    /// <summary>Creates or refreshes the entry. Idempotent; call again when the exe moves.</summary>
    /// <param name="classesRoot">Subkey of HKCU holding the classes; leave default outside tests.</param>
    public static void Register(string exePath, string classesRoot = DefaultClassesRoot)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !Path.IsPathFullyQualified(exePath) || exePath.Contains('"'))
            throw new ArgumentException("An absolute exe path without quotes is required.", nameof(exePath));
        using var verb = Registry.CurrentUser.CreateSubKey(VerbPath(classesRoot), writable: true);
        verb.SetValue("", MenuText);
        verb.SetValue("Icon", $"\"{exePath}\",0");
        verb.SetValue("MultiSelectModel", "Document");
        using var command = verb.CreateSubKey("command", writable: true);
        command.SetValue("", CommandFor(exePath));
    }

    /// <summary>Removes the entry. Idempotent; does nothing when it is absent.</summary>
    public static void Unregister(string classesRoot = DefaultClassesRoot)
    {
        Registry.CurrentUser.DeleteSubKeyTree(VerbPath(classesRoot), throwOnMissingSubKey: false);
    }

    /// <summary>True when the entry exists (and, if <paramref name="exePath"/> is given, points at that exe).</summary>
    public static bool IsRegistered(string? exePath = null, string classesRoot = DefaultClassesRoot)
    {
        using var command = Registry.CurrentUser.OpenSubKey(VerbPath(classesRoot) + @"\command");
        if (command?.GetValue("") is not string value) return false;
        return exePath is null || string.Equals(value, CommandFor(exePath), StringComparison.OrdinalIgnoreCase);
    }
}
