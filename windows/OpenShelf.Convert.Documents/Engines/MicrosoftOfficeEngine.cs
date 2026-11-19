using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenShelf.Convert.Documents;

/// <summary>Converts with the copy of Microsoft Office installed on this PC, through its automation interface (COM), the way
/// Word's own "Save as PDF" does. Office lays the document out itself, so fonts, colours, spacing, headers and footers, columns,
/// tables, pictures and page setup come out exactly as they look in Office. This is the Windows counterpart of the Mac's
/// Quick Look path, and it is asked before LibreOffice and the built-in engines; if Office fails (a damaged or
/// password-protected file, Office not activated) the next engine that accepts the file converts it instead.</summary>
public sealed class MicrosoftOfficeEngine : IConversionEngine
{
    /// <summary>How long Office gets for one file before it is stopped.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private readonly EngineRegistry? _registry;
    private readonly IOfficeAutomation _automation;

    /// <param name="registry">Where to find the engine that takes over when Office fails; null converts with Office only.</param>
    public MicrosoftOfficeEngine(EngineRegistry? registry = null, IOfficeAutomation? automation = null)
    {
        _registry = registry;
        _automation = automation ?? new OfficeAutomation();
    }

    /// <summary>What each Office app writes, given the source's extension.</summary>
    public static IReadOnlyList<OutputFormat> Outputs(OfficeApp app) => app switch
    {
        OfficeApp.Word => [OutputFormat.Pdf, OutputFormat.Docx, OutputFormat.Rtf, OutputFormat.Odt, OutputFormat.Txt],
        OfficeApp.Excel => [OutputFormat.Pdf, OutputFormat.Xlsx, OutputFormat.Ods, OutputFormat.Csv],
        OfficeApp.PowerPoint => [OutputFormat.Pdf, OutputFormat.Pptx, OutputFormat.Jpg, OutputFormat.Png],
        _ => [],
    };

    public bool CanConvert(FileKind kind, OutputFormat format) => kind switch
    {
        FileKind.Document => Outputs(OfficeApp.Word).Contains(format),
        FileKind.Spreadsheet => Outputs(OfficeApp.Excel).Contains(format),
        FileKind.Presentation => Outputs(OfficeApp.PowerPoint).Contains(format),
        _ => false,
    };

    public bool CanConvert(ConversionRequest request)
    {
        var path = FileKinds.ResolveLink(request.SourcePath);
        var extension = FileKinds.ExtensionOf(path);
        if (request.Format.Matches(extension)) return false;
        if (OfficeSources.AppFor(extension) is not { } app || !Outputs(app).Contains(request.Format)) return false;
        return request.ToolsOrDefault.Has(app);
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(FileKinds.ResolveLink(request.SourcePath));
        var app = OfficeSources.AppFor(FileKinds.ExtensionOf(source)) ?? throw ConversionException.Unsupported(request.Format.Title());
        try
        {
            return await ConvertWithOfficeAsync(app, source, request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException && Fallback(request) is { } next)
        {
            OfficeLog.Write($"{app} couldn’t convert {Path.GetFileName(source)} to {request.Format}: {error.Message}; using {next.GetType().Name}");
            return await next.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The engine that would have converted <paramref name="request"/> had Office not been installed.</summary>
    private IConversionEngine? Fallback(ConversionRequest request) =>
        _registry?.Engines.SkipWhile(engine => !ReferenceEquals(engine, this)).Skip(1).FirstOrDefault(engine => engine.CanConvert(request));

    private async Task<IReadOnlyList<string>> ConvertWithOfficeAsync(OfficeApp app, string source, ConversionRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var workspace = Path.Combine(Path.GetTempPath(), "openshelf-msoffice-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(workspace);
        try
        {
            // Slides become pictures by way of a PDF, one picture each, as with LibreOffice.
            var images = request.Format is OutputFormat.Jpg or OutputFormat.Png;
            var format = images ? OutputFormat.Pdf : request.Format;
            var extension = format.FileExtension() ?? throw ConversionException.Unsupported(request.Format.Title());
            var output = Path.Combine(workspace, Path.GetFileNameWithoutExtension(source) + "." + extension);
            await _automation.ConvertAsync(app, source, format, output, Timeout, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(output) || new FileInfo(output).Length == 0)
                throw ConversionException.Failed($"Microsoft {app} couldn’t convert “{Path.GetFileName(source)}”.");
            if (images)
            {
                var label = app == OfficeApp.PowerPoint ? "Slide" : "Page";
                return await Task.Run(() => PdfConversion.Convert(output, request.Format, request.SourceStem, request.OutputPath, label,
                    request.Options.ImageQuality, progress, ct), ct).ConfigureAwait(false);
            }
            var destination = request.OutputPath($"{request.SourceStem}.{extension}");
            File.Move(output, destination);
            progress?.Report(1);
            return [destination];
        }
        finally
        {
            Zips.DeleteFolderQuietly(workspace);
        }
    }
}

/// <summary>Opens a file in an Office app and saves it in another format.</summary>
public interface IOfficeAutomation
{
    Task ConvertAsync(OfficeApp app, string source, OutputFormat format, string output, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Office through late-bound COM on a single-threaded apartment thread of its own. One file at a time per app: Office
/// is slow to start and its automation is not built for several callers. Macros are disabled, nothing is added to Recent, no
/// dialog may appear, and the source opens read-only. An Office process this class started is quit (and, if it hangs past the
/// time limit, ended); an Office the person already had open is left alone apart from closing the file opened here.</summary>
public sealed class OfficeAutomation : IOfficeAutomation
{
    private static readonly Dictionary<OfficeApp, SemaphoreSlim> Gates = new()
    {
        [OfficeApp.Word] = new(1, 1), [OfficeApp.Excel] = new(1, 1), [OfficeApp.PowerPoint] = new(1, 1),
    };

    /// <summary>A password Office is given for every file: a protected file then fails at once instead of asking for one.</summary>
    private const string NoPassword = "openshelf-no-password-7f3a";

    public static string ProcessName(OfficeApp app) => app switch
    {
        OfficeApp.Word => "WINWORD",
        OfficeApp.Excel => "EXCEL",
        _ => "POWERPNT",
    };

    // Word: WdSaveFormat / WdExportFormat.
    public static int? WordFormat(OutputFormat format) => format switch
    {
        OutputFormat.Docx => 16,   // wdFormatDocumentDefault
        OutputFormat.Rtf => 6,     // wdFormatRTF
        OutputFormat.Odt => 23,    // wdFormatOpenDocumentText
        OutputFormat.Txt => 7,     // wdFormatEncodedText (UTF-8 below)
        _ => null,
    };

    // Excel: XlFileFormat.
    public static int? ExcelFormat(OutputFormat format) => format switch
    {
        OutputFormat.Xlsx => 51,   // xlOpenXMLWorkbook
        OutputFormat.Ods => 60,    // xlOpenDocumentSpreadsheet
        OutputFormat.Csv => 62,    // xlCSVUTF8 (the active sheet)
        _ => null,
    };

    // PowerPoint: PpSaveAsFileType.
    public static int? PowerPointFormat(OutputFormat format) => format switch
    {
        OutputFormat.Pdf => 32,    // ppSaveAsPDF
        OutputFormat.Pptx => 24,   // ppSaveAsOpenXMLPresentation
        _ => null,
    };

    public async Task ConvertAsync(OfficeApp app, string source, OutputFormat format, string output, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var gate = Gates[app];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var before = ProcessIds(app);
            var started = new HashSet<int>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    Run(app, source, format, output, () => started.UnionWith(ProcessIds(app).Except(before)));
                    done.TrySetResult();
                }
                catch (Exception e)
                {
                    done.TrySetException(e);
                }
            }) { IsBackground = true, Name = $"OpenShelf {app}" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            var finished = await Task.WhenAny(done.Task, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
            if (finished != done.Task)
            {
                // Hung on a dialog or a damaged file, or the person cancelled: end only the Office this conversion started.
                foreach (var id in started.Count > 0 ? started : ProcessIds(app).Except(before)) Kill(id);
                Zips.DeleteQuietly(output);
                cancellationToken.ThrowIfCancellationRequested();
                throw ConversionException.Failed($"Microsoft {app} took too long to convert “{Path.GetFileName(source)}”.");
            }
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static HashSet<int> ProcessIds(OfficeApp app)
    {
        var processes = Process.GetProcessesByName(ProcessName(app));
        try { return processes.Select(p => p.Id).ToHashSet(); }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    private static void Kill(int id)
    {
        try { using var process = Process.GetProcessById(id); process.Kill(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>Runs on the STA thread.</summary>
    private static void Run(OfficeApp app, string source, OutputFormat format, string output, Action noteStarted)
    {
        var type = Type.GetTypeFromProgID(OfficeInstallation.ProgId(app))
            ?? throw ConversionException.Failed($"Microsoft {app} isn’t installed.");
        dynamic? application = null;
        try
        {
            application = Activator.CreateInstance(type)!;
            noteStarted();
            switch (app)
            {
                case OfficeApp.Word: RunWord(application, source, format, output); break;
                case OfficeApp.Excel: RunExcel(application, source, format, output); break;
                default: RunPowerPoint(application, source, format, output); break;
            }
        }
        catch (COMException e)
        {
            throw ConversionException.Failed($"Microsoft {app} couldn’t convert “{Path.GetFileName(source)}”: {e.Message}");
        }
        finally
        {
            if (application is not null) Release(application);
        }
    }

    private static void RunWord(dynamic word, string source, OutputFormat format, string output)
    {
        // Another Word the person is using may answer CreateInstance; leave it running if it already has documents open.
        var shared = (int)word.Documents.Count > 0;
        if (!shared)
        {
            word.Visible = false;
            word.ScreenUpdating = false;
        }
        word.DisplayAlerts = 0;              // wdAlertsNone
        word.AutomationSecurity = 3;         // msoAutomationSecurityForceDisable: no macros
        dynamic? document = null;
        try
        {
            var missing = Type.Missing;
            // FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate, Revert,
            // WritePasswordDocument, WritePasswordTemplate, Format, Encoding, Visible, OpenAndRepair, DocumentDirection, NoEncodingDialog
            document = word.Documents.Open(source, false, true, false, NoPassword, NoPassword, false, missing, missing, missing, missing, false, false, missing, true);
            if (format == OutputFormat.Pdf)
            {
                // OutputFileName, ExportFormat (PDF), OpenAfterExport, OptimizeFor (print), Range (all), From, To, Item (content),
                // IncludeDocProps, KeepIRM, CreateBookmarks (headings), DocStructureTags, BitmapMissingFonts, UseISO19005_1
                document.ExportAsFixedFormat(output, 17, false, 0, 0, 1, 1, 0, true, true, 1, true, true, false);
            }
            else
            {
                var fileFormat = WordFormat(format) ?? throw ConversionException.Unsupported(format.Title());
                // FileName, FileFormat, LockComments, Password, AddToRecentFiles, WritePassword, ReadOnlyRecommended,
                // EmbedTrueTypeFonts, SaveNativePictureFormat, SaveFormsData, SaveAsAOCELetter, Encoding
                document.SaveAs2(output, fileFormat, false, "", false, "", false, false, false, false, false, 65001);
            }
        }
        finally
        {
            if (document is not null)
            {
                try { document.Close(0); } catch (COMException) { }   // wdDoNotSaveChanges
                Release(document);
            }
            if (!shared) { try { word.Quit(0); } catch (COMException) { } }
        }
    }

    private static void RunExcel(dynamic excel, string source, OutputFormat format, string output)
    {
        var shared = (int)excel.Workbooks.Count > 0;
        if (!shared) excel.Visible = false;
        excel.DisplayAlerts = false;
        excel.AutomationSecurity = 3;
        excel.AskToUpdateLinks = false;
        dynamic? workbook = null;
        try
        {
            var missing = Type.Missing;
            // FileName, UpdateLinks (never), ReadOnly, Format, Password, WriteResPassword, IgnoreReadOnlyRecommended, Origin,
            // Delimiter, Editable, Notify, Converter, AddToMru
            workbook = excel.Workbooks.Open(source, 0, true, missing, NoPassword, missing, true, missing, missing, false, false, missing, false);
            if (format == OutputFormat.Pdf)
            {
                // Type (PDF), Filename, Quality (standard), IncludeDocProperties, IgnorePrintAreas, From, To, OpenAfterPublish
                workbook.ExportAsFixedFormat(0, output, 0, true, false, missing, missing, false);
            }
            else
            {
                var fileFormat = ExcelFormat(format) ?? throw ConversionException.Unsupported(format.Title());
                workbook.SaveAs(output, fileFormat);
            }
        }
        finally
        {
            if (workbook is not null)
            {
                try { workbook.Close(false); } catch (COMException) { }
                Release(workbook);
            }
            if (!shared) { try { excel.Quit(); } catch (COMException) { } }
        }
    }

    private static void RunPowerPoint(dynamic powerPoint, string source, OutputFormat format, string output)
    {
        // PowerPoint runs one instance per user; quit it only if nothing else is open in it.
        var shared = (int)powerPoint.Presentations.Count > 0;
        powerPoint.DisplayAlerts = 1;        // ppAlertsNone
        try { powerPoint.AutomationSecurity = 3; } catch (COMException) { }
        dynamic? presentation = null;
        try
        {
            // FileName, ReadOnly (msoTrue), Untitled (msoFalse), WithWindow (msoFalse): no window at all.
            presentation = powerPoint.Presentations.Open(source + "::" + NoPassword, -1, 0, 0);
            var fileFormat = PowerPointFormat(format) ?? throw ConversionException.Unsupported(format.Title());
            presentation.SaveAs(output, fileFormat);
        }
        finally
        {
            if (presentation is not null)
            {
                try { presentation.Close(); } catch (COMException) { }
                Release(presentation);
            }
            if (!shared && (int)powerPoint.Presentations.Count == 0) { try { powerPoint.Quit(); } catch (COMException) { } }
        }
    }

    private static void Release(object com)
    {
        try { if (Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com); }
        catch (Exception e) when (e is ArgumentException or InvalidComObjectException) { }
    }
}

internal static class OfficeLog
{
    private static readonly object Lock = new();

    public static void Write(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShelf", "logs");
            Directory.CreateDirectory(folder);
            lock (Lock) File.AppendAllText(Path.Combine(folder, "openshelf.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [office] {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
