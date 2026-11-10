using Microsoft.Win32;

namespace OpenShelf.Convert.Markdown;

/// <summary>Prints an HTML file to PDF with the Chromium engine of Microsoft Edge, headless. The Mac renders Markdown to PDF
/// through AppKit rich text; Windows has no such thing in the box, but every supported Windows has Edge. The page brings its
/// own print stylesheet (see <see cref="MarkdownHtml.PrintStylesheet"/>): paper size from <c>@page</c>, margins, page-break
/// rules. Reusable by other engines that have HTML to print.</summary>
public sealed class HtmlPdfPrinter
{
    /// <summary>How long Edge gets before the print is abandoned.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly ToolRunner _runner;
    private readonly Func<string?> _findEdge;

    public HtmlPdfPrinter(ToolRunner? runner = null, Func<string?>? findEdge = null)
    {
        _runner = runner ?? ToolRunners.Default;
        _findEdge = findEdge ?? FindEdge;
    }

    public static HtmlPdfPrinter Default { get; } = new();

    public static string CssPaperName(PaperSize paper) => paper == PaperSize.Letter ? "Letter" : "A4";

    public static string CssMargin(PaperSize paper) => paper == PaperSize.Letter ? "0.8in 0.8in" : "20mm 18mm";

    /// <summary>The Microsoft Edge executable, or null: the App Paths registry key first, then the usual Program Files folders.</summary>
    public static string? FindEdge()
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe");
                    if (key?.GetValue(null) as string is { Length: > 0 } registered && File.Exists(registered.Trim('"'))) return registered.Trim('"');
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
            }
        }
        foreach (var variable in new[] { "ProgramFiles(x86)", "ProgramFiles", "ProgramW6432", "LocalAppData" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(root)) continue;
            var candidate = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public bool IsAvailable => _findEdge() is not null;

    /// <summary>The command-line arguments that print <paramref name="htmlPath"/> to <paramref name="pdfPath"/>.</summary>
    public static IReadOnlyList<string> Arguments(string htmlPath, string pdfPath, string profileFolder) =>
    [
        "--headless=new",
        "--disable-gpu",
        "--no-pdf-header-footer",
        "--print-to-pdf-no-header",
        "--print-to-pdf=" + pdfPath,
        "--user-data-dir=" + profileFolder,
        "--no-first-run",
        "--no-default-browser-check",
        "--disable-extensions",
        "--disable-background-networking",
        "--disable-sync",
        "--hide-scrollbars",
        new Uri(Path.GetFullPath(htmlPath)).AbsoluteUri,
    ];

    /// <summary>Prints the page to <paramref name="pdfPath"/>. Throws <see cref="ConversionException"/> with a message for people
    /// when Edge is missing or fails.</summary>
    public async Task PrintAsync(string htmlPath, string pdfPath, CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        var edge = _findEdge() ?? throw ConversionException.Failed("PDF needs Microsoft Edge, which wasn’t found on this PC. Install or repair Edge and try again.");
        var profile = Path.Combine(Path.GetTempPath(), "openshelf-edge-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(profile);
        try
        {
            try { File.Delete(pdfPath); } catch (IOException) { }
            var invocation = new ToolInvocation(edge, Arguments(htmlPath, pdfPath, profile)) { Timeout = timeout ?? DefaultTimeout };
            try
            {
                await _runner(invocation, null, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessFailure failure) when (failure.TimedOut)
            {
                throw ConversionException.Failed("Microsoft Edge took too long to make the PDF.");
            }
            catch (ProcessFailure failure)
            {
                throw ConversionException.Failed($"Microsoft Edge couldn’t make the PDF{(string.IsNullOrWhiteSpace(failure.Detail) ? "." : ": " + failure.Detail)}");
            }
            catch (MissingToolException)
            {
                throw ConversionException.Failed("PDF needs Microsoft Edge, which wasn’t found on this PC. Install or repair Edge and try again.");
            }
            // Edge can leave before the file is fully on disk; give it a moment.
            for (var attempt = 0; attempt < 20 && !(File.Exists(pdfPath) && new FileInfo(pdfPath).Length > 0); attempt++)
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            if (!File.Exists(pdfPath) || new FileInfo(pdfPath).Length == 0)
                throw ConversionException.Failed("Microsoft Edge didn’t produce a PDF.");
        }
        finally
        {
            await DeleteFolderAsync(profile).ConfigureAwait(false);
        }
    }

    private static async Task DeleteFolderAsync(string folder)
    {
        // Edge's helper processes can hold the profile for a moment after the main process leaves.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { Directory.Delete(folder, true); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Task.Delay(150).ConfigureAwait(false); }
        }
    }
}
