using Microsoft.Win32;

namespace OpenShelf.Convert.Documents;

/// <summary>Finds Microsoft Edge and prints HTML files to PDF with it, headless. Windows has no print-to-file engine of its own
/// that keeps tables, pictures and page breaks, and Edge ships with every supported Windows.</summary>
public sealed class EdgePrinter
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    private readonly ToolRunner _runner;
    private readonly Func<string?> _locate;

    public EdgePrinter(ToolRunner? runner = null, Func<string?>? locate = null)
    {
        _runner = runner ?? ToolRunners.Default;
        _locate = locate ?? Locate;
    }

    public static EdgePrinter Default { get; } = new();

    /// <summary>msedge.exe under Program Files (x86), Program Files, or the App Paths registry key; null if missing.</summary>
    public static string? Locate() => Locate(Environment.GetEnvironmentVariable, File.Exists, ReadAppPath);

    public static string? Locate(Func<string, string?> env, Func<string, bool> exists, Func<string?> appPath)
    {
        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles" })
        {
            var root = env(variable);
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (exists(candidate)) return candidate;
        }
        var registered = appPath();
        return !string.IsNullOrEmpty(registered) && exists(registered) ? registered : null;
    }

    private static string? ReadAppPath()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
                if (key?.GetValue(null) is string value && value.Length > 0) return value.Trim('"');
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }

    public bool IsAvailable => _locate() is not null;

    public static ConversionException Missing() =>
        ConversionException.Failed("Microsoft Edge isn’t installed, and OpenShelf uses it to make PDFs from documents and tables. Install Microsoft Edge and try again.");

    /// <summary>The command line for printing <paramref name="htmlPath"/> to <paramref name="pdfPath"/>.</summary>
    public static List<string> Arguments(string htmlPath, string pdfPath, string profile) =>
    [
        "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
        $"--print-to-pdf={pdfPath}",
        $"--user-data-dir={profile}",
        "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
        "--disable-background-networking", "--disable-component-update", "--hide-scrollbars",
        new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri,
    ];

    public async Task PrintAsync(string htmlPath, string pdfPath, CancellationToken cancellationToken = default)
    {
        var edge = _locate() ?? throw Missing();
        var profile = Path.Combine(Path.GetTempPath(), "openshelf-edge-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(profile);
        try
        {
            Zips.DeleteQuietly(pdfPath);
            try
            {
                await _runner(new ToolInvocation(edge, Arguments(htmlPath, pdfPath, profile)) { Timeout = Timeout }, null, cancellationToken).ConfigureAwait(false);
            }
            catch (MissingToolException)
            {
                throw Missing();
            }
            catch (ProcessFailure failure) when (failure.TimedOut)
            {
                throw ConversionException.Failed("Microsoft Edge took too long to print the document.");
            }
            catch (ProcessFailure)
            {
                throw ConversionException.Failed("Microsoft Edge couldn’t print the document.");
            }
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
            {
                Zips.DeleteQuietly(pdfPath);
                throw ConversionException.Failed("Microsoft Edge couldn’t print the document.");
            }
        }
        finally
        {
            Zips.DeleteFolderQuietly(profile);
        }
    }

    /// <summary>Prints <paramref name="html"/> (a whole page) to <paramref name="pdfPath"/>, through a temporary file.</summary>
    public async Task PrintHtmlAsync(string html, string pdfPath, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openshelf-print-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        try
        {
            var page = Path.Combine(folder, "document.html");
            HtmlWriter.Save(page, html);
            try { await PrintAsync(page, pdfPath, cancellationToken).ConfigureAwait(false); }
            catch { Zips.DeleteQuietly(pdfPath); throw; }
        }
        finally
        {
            Zips.DeleteFolderQuietly(folder);
        }
    }
}
