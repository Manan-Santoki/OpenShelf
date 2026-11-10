using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenShelf.Convert;

public enum ConversionErrorKind { Unsupported, Unreadable, Failed }

/// <summary>A conversion failure whose message is already fit to show a person.</summary>
public sealed class ConversionException : Exception
{
    public ConversionErrorKind Kind { get; }

    private ConversionException(ConversionErrorKind kind, string message) : base(message) => Kind = kind;

    public static ConversionException Unsupported(string detail) =>
        new(ConversionErrorKind.Unsupported, $"This conversion isn’t supported: {detail}.");

    public static ConversionException Unreadable(string path) =>
        new(ConversionErrorKind.Unreadable, $"“{Path.GetFileName(path)}” couldn’t be read.");

    public static ConversionException Failed(string message) => new(ConversionErrorKind.Failed, message);
}

/// <summary>An optional tool that was there when the conversion was offered has since been uninstalled.</summary>
public sealed class MissingToolException : Exception
{
    /// <summary>How people know the tool: "ffmpeg", "LibreOffice".</summary>
    public string Name { get; }

    public MissingToolException(string name) : base($"{name} isn’t installed any more.") => Name = name;

    public static MissingToolException ForExecutable(string executable)
    {
        var file = Path.GetFileNameWithoutExtension(executable);
        return new MissingToolException(file.Equals("soffice", StringComparison.OrdinalIgnoreCase) ? "LibreOffice" : file);
    }
}

/// <summary>A tool ran and failed.</summary>
public sealed class ProcessFailure : Exception
{
    public string Tool { get; }
    /// <summary>The tool's last error line, raw.</summary>
    public string Detail { get; }
    public int ExitCode { get; }
    /// <summary>Whether the tool crashed (an access violation or the like) rather than exiting with an error of its own.</summary>
    public bool Crashed { get; }
    public bool TimedOut { get; }

    public ProcessFailure(string tool, string detail, int exitCode = 1, bool crashed = false, bool timedOut = false)
        : base(Describe(tool, detail, crashed, timedOut))
    {
        Tool = tool;
        Detail = detail;
        ExitCode = exitCode;
        Crashed = crashed;
        TimedOut = timedOut;
    }

    private static string Describe(string tool, string detail, bool crashed, bool timedOut)
    {
        if (timedOut) return $"{tool} took too long and was stopped.";
        if (crashed) return $"{tool} crashed.";
        return detail.Length == 0 ? $"{tool} failed." : $"{tool} failed: {FailureMessage.Sanitized(detail)}";
    }
}

/// <summary>How to run a child process.</summary>
public sealed record ProcessOptions
{
    public string? WorkingDirectory { get; init; }
    /// <summary>Added to the minimal environment (see <see cref="ChildEnvironment"/>).</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
    /// <summary>A file fed to the tool's standard input; otherwise the input is closed at once.</summary>
    public string? StandardInputFile { get; init; }
    /// <summary>A file the tool's standard output is written to (instead of being captured).</summary>
    public string? StandardOutputFile { get; init; }
    public Action<string>? OnOutputLine { get; init; }
    public Action<string>? OnErrorLine { get; init; }
    /// <summary>The tree is killed and <see cref="ProcessFailure.TimedOut"/> thrown when this passes.</summary>
    public TimeSpan? Timeout { get; init; }
    /// <summary>Captured output keeps the last this many lines of each stream.</summary>
    public int MaxCapturedLines { get; init; } = 2000;
    /// <summary>... and at most this many characters.</summary>
    public int MaxCapturedCharacters { get; init; } = 256 * 1024;
    /// <summary>When false a non-zero exit is returned in the result instead of thrown.</summary>
    public bool ThrowOnFailure { get; init; } = true;
}

public sealed record ProcessResult(int ExitCode, string Output, string Error, string LastErrorLine);

/// <summary>One run of a command-line tool as the optional-tool engines describe it: arguments are always a list, never
/// a shell string, and files are always absolute paths, so a name starting with "-" cannot be read as an option.</summary>
public sealed record ToolInvocation(string Executable, IReadOnlyList<string> Arguments)
{
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Starts a tool and passes on each line it prints (standard output and error), for progress.
/// <see cref="ToolRunners.Default"/> normally; tests swap in a fake that records the invocation and writes what the
/// tool would have.</summary>
public delegate Task ToolRunner(ToolInvocation invocation, Action<string>? onLine, CancellationToken cancellationToken);

public static class ToolRunners
{
    public static ToolRunner Default { get; } = (invocation, onLine, ct) => ProcessRunner.RunAsync(
        invocation.Executable, invocation.Arguments,
        new ProcessOptions
        {
            WorkingDirectory = invocation.WorkingDirectory,
            ExtraEnvironment = invocation.ExtraEnvironment,
            Timeout = invocation.Timeout,
            OnOutputLine = onLine,
            OnErrorLine = onLine,
        }, ct);
}

/// <summary>Runs command-line tools without a console window, with bounded output capture, a timeout, and cancellation that
/// kills the whole process tree through a job object.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, ProcessOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ProcessOptions();
        // Launching a missing file fails with a cryptic message; an optional tool may have been uninstalled since it was detected.
        if (!File.Exists(executable))
        {
            ExternalToolsProvider.Default.Refresh();
            throw MissingToolException.ForExecutable(executable);
        }
        cancellationToken.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (options.WorkingDirectory is not null) startInfo.WorkingDirectory = options.WorkingDirectory;
        ChildEnvironment.Apply(startInfo, options.ExtraEnvironment);

        var output = new BoundedLines(options.MaxCapturedLines, options.MaxCapturedCharacters);
        var error = new BoundedLines(options.MaxCapturedLines, options.MaxCapturedCharacters);

        using var job = new JobObject();
        using var process = new Process { StartInfo = startInfo };
        var timedOut = false;
        using var timeoutSource = new CancellationTokenSource();
        if (options.Timeout is { } timeout) timeoutSource.CancelAfter(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        var assigned = false;
        void KillTree()
        {
            try
            {
                if (assigned) job.Terminate();
                else if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        if (!process.Start()) throw new ProcessFailure(Path.GetFileName(executable), "it could not be started");
        assigned = job.Assign(process);
        using var registration = linked.Token.Register(() =>
        {
            timedOut = timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            KillTree();
        });

        // Standard input: a file, or closed at once so a tool never waits for typing.
        var inputTask = Task.Run(async () =>
        {
            try
            {
                if (options.StandardInputFile is not null)
                {
                    await using var input = File.OpenRead(options.StandardInputFile);
                    await input.CopyToAsync(process.StandardInput.BaseStream, CancellationToken.None);
                }
                process.StandardInput.Close();
            }
            catch (IOException) { /* the tool closed its input early */ }
        });

        // Each pipe is read to its end on its own task, and the run waits for that after the tool exits, so the tool's last
        // words (the error that decides a fallback, a last progress line) are never missed.
        var outputTask = options.StandardOutputFile is { } outputFile
            ? CopyToFileAsync(process.StandardOutput.BaseStream, outputFile)
            : ReadLinesAsync(process.StandardOutput, output, options.OnOutputLine);
        var errorTask = ReadLinesAsync(process.StandardError, error, options.OnErrorLine);

        await process.WaitForExitAsync(CancellationToken.None);
        // A process the tool started may keep a pipe open; do not wait on it for long, then end it.
        var readers = Task.WhenAll(outputTask, errorTask, inputTask);
        if (await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(2))) != readers)
        {
            job.Terminate();
            await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(1)));
        }
        job.Dispose();

        if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
        var tool = Path.GetFileName(executable);
        if (timedOut) throw new ProcessFailure(tool, "", process.ExitCode, timedOut: true);

        var exitCode = process.ExitCode;
        var result = new ProcessResult(exitCode, output.Text, error.Text, error.LastMeaningfulLine());
        if (exitCode != 0 && options.ThrowOnFailure)
            throw new ProcessFailure(tool, result.LastErrorLine, exitCode, crashed: unchecked((uint)exitCode) >= 0xC0000000);
        return result;
    }

    private static async Task ReadLinesAsync(StreamReader reader, BoundedLines sink, Action<string>? onLine)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                sink.Add(line);
                onLine?.Invoke(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    private static async Task CopyToFileAsync(Stream source, string path)
    {
        try
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await source.CopyToAsync(file);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }

    /// <summary>Whether <paramref name="line"/> is one of Chromium's own log lines, as calibre's PDF output writes them:
    /// "[60878:259:1101/181024.142716:ERROR:service_utils.cc(339)] ...".</summary>
    public static bool IsChromiumLog(string line)
    {
        if (!line.StartsWith('[')) return false;
        var close = line.IndexOf(']');
        if (close < 0) return false;
        var fields = line[1..close].Split(':');
        return fields.Length >= 5 && fields[0].All(char.IsDigit)
            && fields[3] is "INFO" or "WARNING" or "ERROR" or "FATAL" or "VERBOSE1";
    }

    /// <summary>The last lines (and at most so many characters) of a stream.</summary>
    private sealed class BoundedLines(int maxLines, int maxCharacters)
    {
        private readonly object _lock = new();
        private readonly LinkedList<string> _lines = new();
        private int _characters;

        public void Add(string line)
        {
            lock (_lock)
            {
                _lines.AddLast(line);
                _characters += line.Length + 1;
                while (_lines.Count > 1 && (_lines.Count > maxLines || _characters > maxCharacters))
                {
                    _characters -= _lines.First!.Value.Length + 1;
                    _lines.RemoveFirst();
                }
            }
        }

        public string Text
        {
            get { lock (_lock) return string.Join('\n', _lines); }
        }

        /// <summary>Chromium inside calibre logs "[pid:tid:date:ERROR:file(line)] ..." after the real error; that is never the reason a tool failed.</summary>
        public string LastMeaningfulLine()
        {
            lock (_lock)
            {
                var lines = _lines.Where(l => l.Trim().Length > 0).ToList();
                var line = lines.LastOrDefault(l => !IsChromiumLog(l)) ?? lines.LastOrDefault();
                return line?.Trim() ?? "";
            }
        }
    }
}
