using System.Windows;

namespace OpenShelf.App;

/// <summary>
/// Composition root. The orchestrator owns this file: it creates the services
/// in Contracts.cs and hands them to each other. Feature code lives in its own
/// folder (Drag/, Wheel/, Shelf/, Tray/, Toast/) and never edits this file.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppHost.Start(this);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppHost.Stop();
        base.OnExit(e);
    }
}
