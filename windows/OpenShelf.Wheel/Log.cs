using System.IO;

namespace OpenShelf.Wheel;

/// <summary>Tiny file logger: %LOCALAPPDATA%\OpenShelf\logs\openshelf.log. Never throws.</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (_path is null)
                {
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShelf", "logs");
                    Directory.CreateDirectory(dir);
                    _path = Path.Combine(dir, "openshelf.log");
                }
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [wheel] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }

    public static void Error(string where, Exception ex) => Write($"EXCEPTION in {where}: {ex}");
}
