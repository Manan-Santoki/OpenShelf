using System.Diagnostics;

namespace OpenShelf.Convert;

/// <summary>The environment every program OpenShelf starts gets: a short allowlist from the app's own, plus whatever a tool
/// needs. An app started from a terminal inherits that shell's whole environment (tokens, cloud keys), and none of it
/// should reach ffmpeg, calibre or LibreOffice. Port of ChildEnvironment.swift.</summary>
public static class ChildEnvironment
{
    /// <summary>Passed through when set (Windows names are case-insensitive).</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SystemRoot", "SystemDrive", "windir", "TEMP", "TMP", "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA",
        "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)",
        "CommonProgramW6432", "ALLUSERSPROFILE", "PUBLIC", "COMSPEC", "PATHEXT", "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS",
        "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "LANG", "TERM",
    };

    /// <summary>Where tools look for other tools: the system folders and OpenShelf's own copies. Never the inherited PATH.</summary>
    public static string SearchPath(IReadOnlyDictionary<string, string>? baseEnvironment = null)
    {
        string Get(string name) => baseEnvironment is null
            ? Environment.GetEnvironmentVariable(name) ?? ""
            : baseEnvironment.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value ?? "";
        var systemRoot = Get("SystemRoot") is { Length: > 0 } root ? root : @"C:\Windows";
        var directories = new List<string>
        {
            Path.Combine(systemRoot, "System32"),
            systemRoot,
            Path.Combine(systemRoot, "System32", "Wbem"),
        };
        var own = ToolLocations.ForEnvironment(Get).OwnToolsRoot;
        if (own.Length > 0 && Directory.Exists(own))
        {
            foreach (var tool in Directory.EnumerateDirectories(own))
            {
                directories.Add(tool);
                directories.Add(Path.Combine(tool, "bin"));
            }
        }
        return string.Join(';', directories);
    }

    /// <summary>The allowlisted part of <paramref name="baseEnvironment"/> (and its LC_* settings), the search path,
    /// then <paramref name="extra"/> on top.</summary>
    public static Dictionary<string, string> Minimal(IReadOnlyDictionary<string, string>? baseEnvironment = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        var source = baseEnvironment ?? CurrentEnvironment();
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if ((Allowed.Contains(key) || key.StartsWith("LC_", StringComparison.OrdinalIgnoreCase)) && !IsCredential(key))
                environment[key] = value;
        }
        environment["PATH"] = SearchPath(baseEnvironment);
        if (extra is not null)
            foreach (var (key, value) in extra) environment[key] = value;
        return environment;
    }

    /// <summary>Names that look like credentials, which never pass.</summary>
    public static bool IsCredential(string name)
    {
        var upper = name.ToUpperInvariant();
        string[] fragments = ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "_PASS", "KEY", "AUTH", "CREDENTIAL", "SESSION", "COOKIE"];
        string[] prefixes = ["AWS_", "GITHUB_", "GH_", "OP_", "SSH_"];
        return fragments.Any(upper.Contains) || prefixes.Any(upper.StartsWith);
    }

    /// <summary>Replaces <paramref name="startInfo"/>'s environment with the minimal one.</summary>
    public static void Apply(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string>? extra = null)
    {
        startInfo.Environment.Clear();
        foreach (var (key, value) in Minimal(extra: extra)) startInfo.Environment[key] = value;
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value) result[key] = value;
        return result;
    }
}
