using System.IO;
using System.Text;

namespace OpenShelf.Shelf;

/// <summary>Tiny append-only logger shared by the shelf, toast and tray code.</summary>
internal static class Log
{
    static readonly object Gate = new();
    static string? _path;

    static string LogPath
    {
        get
        {
            if (_path != null) return _path;
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShelf", "logs");
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            return _path = Path.Combine(dir, "openshelf.log");
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : message + ": " + ex);

    static void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [Shelf] {message}{Environment.NewLine}";
            lock (Gate)
            {
                using var fs = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var bytes = Encoding.UTF8.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception)
        {
            // Logging must never throw.
        }
    }
}
