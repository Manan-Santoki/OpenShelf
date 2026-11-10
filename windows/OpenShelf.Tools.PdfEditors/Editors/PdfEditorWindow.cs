using System.Windows;

namespace OpenShelf.Tools.PdfEditors;

/// <summary>What the four PDF editors have in common: a thin progress bar under the controls while a save runs, a cancel that
/// stops the work (closing the window cancels the save, and the headless operations leave no partial file), and the demo hook.</summary>
public abstract class PdfEditorWindow : EditorWindow, IPdfEditorDemo
{
    private readonly SlimProgress _bar = new();

    protected PdfEditorWindow(EditorHost host, string title, Size contentSize) : base(host, title, contentSize)
    {
        Controls = _bar;
    }

    /// <summary>Reports how far a save has got; safe from any thread.</summary>
    protected IProgress<double> Reporter => new Progress<double>(value => Dispatcher.BeginInvoke(() => _bar.Value = value));

    protected override bool PrepareToSave()
    {
        _bar.IsActive = true;
        BeginWork();
        return true;
    }

    /// <summary>Saving started: lock whatever the user must not change meanwhile.</summary>
    protected virtual void BeginWork() { }

    /// <summary>Saving ended (done, failed or cancelled): unlock it again.</summary>
    protected virtual void EndWork() { }

    protected sealed override SaveOutcome Save(IProgress<double> progress, CancellationToken cancel)
    {
        try { return SaveCore(Reporter, cancel); }
        finally
        {
            Dispatcher.BeginInvoke(() =>
            {
                _bar.IsActive = false;
                EndWork();
            });
        }
    }

    /// <summary>Writes the results on a worker thread. Failures a person can act on are <see cref="SaveException"/>s.</summary>
    protected abstract SaveOutcome SaveCore(IProgress<double> progress, CancellationToken cancel);

    public virtual void ApplyDemoState(string state) { }
}
