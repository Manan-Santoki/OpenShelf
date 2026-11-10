using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace OpenShelf.Tools.MediaEditors;

/// <summary>
/// What every media editor shares: the engine, a progress card under the controls with a Cancel button, exports that run off the UI
/// thread and report into the card, and a token that ends when the window closes. A subclass says what the primary button reads,
/// when it is enabled, and what exporting does.
/// </summary>
internal abstract class MediaEditorWindow : EditorWindow
{
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _export;
    private double _pending = -1;
    private string? _pendingDetail;

    protected MediaEditorWindow(EditorHost host, IMediaOperations operations, string title, Size contentSize) : base(host, title, contentSize)
    {
        Operations = operations;
        Card = new ExportCard(() => _export?.Cancel());
        Closed += (_, _) =>
        {
            _lifetime.Cancel();
            _export?.Cancel();
            Disposed();
        };
        PreviewKeyDown += OnSpace;
    }

    protected IMediaOperations Operations { get; }

    /// <summary>The progress card; put it last in <c>Controls</c>.</summary>
    protected ExportCard Card { get; }

    /// <summary>Ends when the window closes; previews being loaded stop with it.</summary>
    protected CancellationToken Lifetime => _lifetime.Token;

    /// <summary>The window closed: stop players and drop pictures.</summary>
    protected virtual void Disposed() { }

    /// <summary>Space plays or pauses (Trim); other editors ignore it.</summary>
    protected virtual void SpacePressed() { }

    protected virtual bool HandlesSpace => false;

    private void OnSpace(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Space || !HandlesSpace) return;
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox or ComboBox) return;
        e.Handled = true;
        SpacePressed();
    }

    // ---------------------------------------------------------------- the button

    protected abstract string PrimaryText { get; }
    protected abstract bool CanExport { get; }

    protected sealed override string PrimaryTitle
    {
        get
        {
            // Refresh runs when saving starts and ends: the card follows.
            if (!IsBusy) Card.Hide();
            return IsBusy ? BusyText : PrimaryText;
        }
    }

    /// <summary>The button while exporting.</summary>
    protected virtual string BusyText => "Saving…";

    protected sealed override bool CanApply => CanExport;

    // ---------------------------------------------------------------- exporting

    /// <summary>Writes the results. Runs off the UI thread; report with <see cref="Report"/>. Honour the token: it ends when the card's
    /// Cancel is pressed or the window closes.</summary>
    protected abstract Task<SaveOutcome> ExportAsync(CancellationToken cancel);

    protected override bool PrepareToSave()
    {
        _export?.Dispose();
        _export = new CancellationTokenSource();
        Card.Show(0, FirstDetail());
        return true;
    }

    /// <summary>Presses the primary button (demos and tests).</summary>
    internal void ApplyForDemo() => Apply();

    /// <summary>Presses the progress card's Cancel (demos and tests).</summary>
    internal void CancelExportForDemo() => _export?.Cancel();

    /// <summary>Text for the card before the first report.</summary>
    protected virtual string? FirstDetail() => null;

    protected sealed override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, _export!.Token);
        return ExportAsync(linked.Token).GetAwaiter().GetResult();
    }

    /// <summary>Shows how far the export is (0 to 1) and, optionally, what it is doing ("Exporting 2 of 3…"). Safe from any thread;
    /// reports that arrive faster than the screen draws are folded into the latest.</summary>
    protected void Report(double fraction, string? detail = null)
    {
        var schedule = Interlocked.Exchange(ref _pending, fraction) < 0;
        _pendingDetail = detail;
        if (!schedule) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            var value = Interlocked.Exchange(ref _pending, -1);
            // A late report must not bring back a finished card.
            if (value >= 0 && IsBusy) Card.Show(value, _pendingDetail);
        });
    }

    /// <summary>"Exporting 2 of 3…" for batches; null for one file.</summary>
    protected static string? BatchDetail(int index, int count) => count > 1 ? $"Exporting {index + 1} of {count}…" : null;
}
