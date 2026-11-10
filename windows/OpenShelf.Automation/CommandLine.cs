using System.Text;
using OpenShelf.Convert;

namespace OpenShelf.Automation;

public enum CommandKind { Convert, Shelf, Wheel }

/// <summary>What a command-line invocation (or the Explorer verb) asks the running app to do.</summary>
/// <param name="Format">Only for <see cref="CommandKind.Convert"/>.</param>
public sealed record AutomationCommand(CommandKind Kind, IReadOnlyList<string> Paths, OutputFormat? Format = null);

public enum ParseOutcome
{
    /// <summary>No arguments: start normally.</summary>
    None,
    /// <summary>A command to run.</summary>
    Command,
    /// <summary>Print <see cref="ParseResult.Message"/> to stdout, exit 0.</summary>
    Help,
    /// <summary>Print <see cref="ParseResult.Message"/> to stderr, exit 2.</summary>
    Error,
}

public sealed record ParseResult(ParseOutcome Outcome, AutomationCommand? Command = null, string? Message = null)
{
    public int ExitCode => Outcome == ParseOutcome.Error ? 2 : 0;
}

/// <summary>Pure argument parser for <c>OpenShelf.exe convert --to jpg files…</c>, <c>shelf files…</c>, <c>wheel files…</c>, <c>--help</c>.</summary>
public static class CommandLine
{
    /// <summary>Names accepted besides the ids in <see cref="OutputFormats.Id"/>.</summary>
    private static readonly Dictionary<string, OutputFormat> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jpeg"] = OutputFormat.Jpg, ["jpe"] = OutputFormat.Jpg, ["tif"] = OutputFormat.Tiff, ["heif"] = OutputFormat.Heic,
        ["htm"] = OutputFormat.Html, ["markdown"] = OutputFormat.Md, ["text"] = OutputFormat.Txt, ["aif"] = OutputFormat.Aiff,
        ["tar.gz"] = OutputFormat.Tgz, ["unzip"] = OutputFormat.Extract, ["oga"] = OutputFormat.Ogg,
    };

    /// <summary>Formats the command line accepts: everything the wheel can write on Windows (no Pages, Numbers, Keynote).</summary>
    public static IReadOnlyList<OutputFormat> ValidFormats { get; } =
        OutputFormats.All.Where(f => f is not (OutputFormat.Pages or OutputFormat.Numbers or OutputFormat.Key)).ToList();

    public static OutputFormat? ParseFormat(string name)
    {
        name = name.Trim().TrimStart('.');
        if (Aliases.TryGetValue(name, out var alias)) return alias;
        return OutputFormats.FromId(name) is { } f && ValidFormats.Contains(f) ? f : null;
    }

    public const string Help = """
        OpenShelf command line

        Usage:
          OpenShelf.exe                                   Start OpenShelf (or show Settings if it is running)
          OpenShelf.exe convert --to <format> <files...>  Convert the files and save the results next to them
          OpenShelf.exe shelf <files...>                  Put the files on a shelf
          OpenShelf.exe wheel <files...>                  Open the Convert wheel at the pointer for the files
          OpenShelf.exe --help                            Show this text

        Options:
          -t, --to <format>   Target format for convert, e.g. jpg, png, pdf, mp4, mp3, zip
          --                  Treat everything after it as a file name

        Relative paths are taken from the current folder. The commands are handed to the running
        OpenShelf, which is started first if needed.
        """;

    /// <param name="args">Arguments without the exe path.</param>
    /// <param name="currentDirectory">Base for relative paths.</param>
    /// <param name="exists">Whether a file or folder exists; defaults to the disk. Injected so the parser stays pure.</param>
    public static ParseResult Parse(IReadOnlyList<string> args, string currentDirectory, Func<string, bool>? exists = null)
    {
        exists ??= p => File.Exists(p) || Directory.Exists(p);
        if (args.Count == 0) return new ParseResult(ParseOutcome.None);

        var first = args[0];
        if (IsHelp(first)) return new ParseResult(ParseOutcome.Help, Message: Help);

        CommandKind kind;
        switch (first.ToLowerInvariant())
        {
            case "convert": kind = CommandKind.Convert; break;
            case "shelf": kind = CommandKind.Shelf; break;
            case "wheel": kind = CommandKind.Wheel; break;
            default:
                return Error($"Unknown command '{first}'. Commands: convert, shelf, wheel. Try --help.");
        }

        string? formatName = null;
        var files = new List<string>();
        var endOfOptions = false;
        for (var i = 1; i < args.Count; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--") { endOfOptions = true; continue; }
            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                if (IsHelp(arg)) return new ParseResult(ParseOutcome.Help, Message: Help);
                if (kind == CommandKind.Convert && arg is "--to" or "-t")
                {
                    if (i + 1 >= args.Count) return Error($"{arg} needs a format name. {FormatList()}");
                    formatName = args[++i];
                    continue;
                }
                if (kind == CommandKind.Convert && arg.StartsWith("--to=", StringComparison.Ordinal))
                {
                    formatName = arg["--to=".Length..];
                    continue;
                }
                return Error($"Unknown option '{arg}'. Try --help.");
            }
            files.Add(arg);
        }

        OutputFormat? format = null;
        if (kind == CommandKind.Convert)
        {
            if (formatName is null) return Error($"convert needs --to <format>. {FormatList()}");
            format = ParseFormat(formatName);
            if (format is null) return Error($"Unknown format '{formatName}'.{Suggestion(formatName)} {FormatList()}");
        }

        if (files.Count == 0) return Error($"{kind.ToString().ToLowerInvariant()} needs at least one file. Try --help.");

        var paths = new List<string>();
        foreach (var file in files)
        {
            string full;
            try { full = Path.GetFullPath(file, currentDirectory); }
            catch (Exception) { return Error($"'{file}' is not a valid path."); }
            if (!exists(full)) return Error($"File not found: {full}");
            if (!paths.Contains(full, StringComparer.OrdinalIgnoreCase)) paths.Add(full);
        }
        return new ParseResult(ParseOutcome.Command, new AutomationCommand(kind, paths, format));
    }

    private static bool IsHelp(string arg) => arg is "--help" or "-h" or "-?" or "/?" or "/h" or "help";

    private static ParseResult Error(string message) => new(ParseOutcome.Error, Message: message);

    private static string FormatList() =>
        "Valid formats: " + string.Join(", ", ValidFormats.Select(f => f.Id())) + ".";

    private static string Suggestion(string given)
    {
        var best = ValidFormats.Select(f => f.Id()).Concat(Aliases.Keys)
            .Select(id => (id, d: Distance(given.ToLowerInvariant(), id)))
            .OrderBy(x => x.d).First();
        return best.d <= 2 ? $" Did you mean '{best.id}'?" : "";
    }

    private static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return row[b.Length];
    }
}
