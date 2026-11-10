namespace OpenShelf.Convert;

/// <summary>Converts files of some kinds to some formats. Engines register into an <see cref="EngineRegistry"/>;
/// the <see cref="Converter"/> hands each request to the first engine that accepts it.</summary>
public interface IConversionEngine
{
    /// <summary>Whether this engine makes <paramref name="format"/> from files of <paramref name="kind"/>. Cheap and pure: no I/O.</summary>
    bool CanConvert(FileKind kind, OutputFormat format);

    /// <summary>Engines that need more than the kind (the source's extension, the tools installed) override this; the
    /// converter asks this one. The default is the kind-only question.</summary>
    bool CanConvert(ConversionRequest request) => CanConvert(FileKinds.Of(request.SourcePath), request.Format);

    /// <summary>Converts, writing results with <see cref="ConversionRequest.OutputPath"/> (never to a name of its own
    /// choosing, never over an existing file). Returns the files and folders written. Report 0..1 progress for the file, throw
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled, and clean up
    /// partial output on failure (the runner deletes its staging folder as well).</summary>
    Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>The engines available, in the order they are asked. Port of the dispatch in Converter.swift.</summary>
public sealed class EngineRegistry
{
    private readonly List<(int Priority, int Order, IConversionEngine Engine)> _engines = [];
    private readonly object _lock = new();

    /// <summary>The engines OpenShelf ships. Later waves add theirs in <see cref="BuiltIn"/>.</summary>
    public static EngineRegistry Default { get; } = BuiltIn();

    public static EngineRegistry BuiltIn()
    {
        var registry = new EngineRegistry();
        registry.Register(new ArchiveEngine());
        registry.Register(new ImageEngine());
        registry.Register(new SubtitleEngine());
        registry.Register(new SpreadsheetEngine());
        return registry;
    }

    /// <summary>Adds an engine. Higher <paramref name="priority"/> engines are asked first (calibre before Pandoc for a
    /// book, say); equal priorities are asked in registration order.</summary>
    public void Register(IConversionEngine engine, int priority = 0)
    {
        lock (_lock) _engines.Add((priority, _engines.Count, engine));
    }

    public IReadOnlyList<IConversionEngine> Engines
    {
        get { lock (_lock) return Ordered(); }
    }

    private List<IConversionEngine> Ordered() =>
        _engines.OrderByDescending(e => e.Priority).ThenBy(e => e.Order).Select(e => e.Engine).ToList();

    /// <summary>The first engine that accepts <paramref name="request"/>, or null.</summary>
    public IConversionEngine? Find(ConversionRequest request)
    {
        List<IConversionEngine> engines;
        lock (_lock) engines = Ordered();
        return engines.FirstOrDefault(engine => engine.CanConvert(request));
    }

    public bool CanConvert(FileKind kind, OutputFormat format) => Engines.Any(engine => engine.CanConvert(kind, format));
}

/// <summary>Converts one file into another format. Picks the engine, gives every engine one consistent view of the
/// installed tools, and turns failures into messages a person can act on.</summary>
public sealed class Converter(EngineRegistry registry, ExternalToolsProvider tools)
{
    public Converter() : this(EngineRegistry.Default, ExternalToolsProvider.Default) { }

    /// <summary>The app-wide converter (default engines, default tool detection).</summary>
    public static Converter Default { get; } = new();

    public EngineRegistry Registry => registry;
    public ExternalToolsProvider Tools => tools;

    /// <summary>Whether some engine makes this conversion. A false answer is the "not supported" case.</summary>
    public bool CanConvert(ConversionRequest request) => registry.Find(request) is not null;

    /// <summary>Returns the files written (several when, say, a multi-page PDF becomes images).</summary>
    public async Task<IReadOnlyList<string>> ConvertAsync(ConversionRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        request = request with { Tools = request.Tools ?? tools.Current };
        try
        {
            var engine = registry.Find(request)
                ?? throw ConversionException.Unsupported(Describe(request));
            return await engine.ConvertAsync(request, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw FailureMessage.Describe(error, request.SourcePath);
        }
    }

    private static string Describe(ConversionRequest request)
    {
        var extension = FileKinds.ExtensionOf(request.SourcePath).ToUpperInvariant();
        return $"{(extension.Length == 0 ? "this file" : extension)} to {request.Format.Title()}";
    }
}
