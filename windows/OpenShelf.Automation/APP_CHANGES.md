# Changes needed in OpenShelf.App

Not applied. Apply by hand when OpenShelf.App is free.

## 0. Project reference

`OpenShelf.App.csproj`: add `<ProjectReference Include="..\OpenShelf.Automation\OpenShelf.Automation.csproj" />`.
(The .sln needs the two new projects too: `OpenShelf.Automation` and `OpenShelf.Automation.Tests`.)

## 1. IWheelService: no change needed

`WheelService.OpenKeyboardWheel(IReadOnlyList<string> files)` already opens the Convert wheel at the pointer for known
files. Keep the concrete `WheelService` in a static field (`AppHost.WheelHost`) and call that. If you prefer to go
through the interface, add `void OpenForPaths(IReadOnlyList<string> paths);` to `IWheelService` (Contracts.cs) and
implement it as `=> OpenKeyboardWheel(paths);`.

## 2. App.xaml.cs (replace `OnStartup`, `SignalRunningCopy`, `ListenForSecondLaunch`)

```csharp
using OpenShelf.Automation;
// existing usings stay

protected override void OnStartup(StartupEventArgs e)
{
    base.OnStartup(e);

    // --help and bad arguments never need the running copy or a mutex.
    var parsed = CommandLine.Parse(e.Args, Environment.CurrentDirectory);
    if (parsed.Outcome is ParseOutcome.Help or ParseOutcome.Error)
    {
        CliConsole.TryWrite(parsed.Message ?? "", parsed.Outcome == ParseOutcome.Error);
        Shutdown(parsed.ExitCode);
        return;
    }

    _mutex = new Mutex(initiallyOwned: true, InstanceName, out var first);
    if (!first)
    {
        SignalRunningCopy(parsed.Command);
        Shutdown();
        return;
    }

    DispatcherUnhandledException += (_, args) => { Log(args.Exception); args.Handled = true; };

    AppHost.Start(this);
    ListenForSecondLaunch();
    if (parsed.Command is { } command) AppHost.Execute(command);   // first launch started by Explorer or the command line
}

private static void SignalRunningCopy(AutomationCommand? command)
{
    try
    {
        // A copy that is still starting has not opened the pipe yet; commands are worth a few retries.
        var attempts = command is null ? 1 : 10;
        for (var i = 0; i < attempts; i++)
        {
            if (PipeProtocol.SendAsync(command, 800, PipeProtocol.PipeName).GetAwaiter().GetResult()) return;
            Thread.Sleep(300);
        }
    }
    catch { /* the first copy is shutting down; nothing to do */ }
}

private void ListenForSecondLaunch()
{
    _pipeCancel = new CancellationTokenSource();
    var token = _pipeCancel.Token;
    Task.Run(async () =>
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var request = await PipeProtocol.AcceptAsync(PipeProtocol.PipeName, TimeSpan.FromSeconds(2), token);
                switch (request)
                {
                    case ShowSettingsRequest: _ = Dispatcher.BeginInvoke(AppHost.ShowSettingsFromSecondLaunch); break;
                    case CommandRequest c: AppHost.Execute(c.Command); break;   // coalesces; marshals to the UI thread itself
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log(ex); await Task.Delay(500, CancellationToken.None); }
        }
    }, token);
}
```

`PipeProtocol.AcceptAsync` creates the server stream (`PipeDirection.In`, 1 instance, async), exactly as before. A
legacy client (one byte `1`) or an empty connection still means "show Settings", so old and new copies interoperate.

`OpenShelf.exe` is a GUI-subsystem exe, so `--help` text is written to the parent console through `CliConsole`
(it attaches with `AttachConsole(ATTACH_PARENT_PROCESS)`); started from Explorer, nothing is printed. If you want
a fallback there, show a `MessageBox` with `parsed.Message` when `TryWrite` returns false.

## 3. AppHost.cs

New usings: `using OpenShelf.Automation;`. New members and wiring:

```csharp
public static WheelService? WheelHost { get; private set; }
private static CommandCoalescer? _coalescer;
private static WatchedFolderService? _watched;

// in Start(), after `var wheels = new WheelService(...)`:
WheelHost = wheels;

_coalescer = new CommandCoalescer(command =>
    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => RunCommand(command)));

_watched = WatchedFolderService.ForPreferences(_store, arrival =>
    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OnFolderArrival(arrival)));
catalog.Saved += _watched.NoteOutputs;     // results OpenShelf wrote never count as arrivals
_watched.Start();                           // after the tray and wheels are up, end of Start()

// keep the Explorer entry pointing at this exe (the Settings toggle stores "true"/"false" under key "explorerEntry")
SyncExplorerEntry();

// in Stop(), first lines:
_watched?.Dispose();
_coalescer?.Dispose();
```

```csharp
/// <summary>Entry point for the command line, the Explorer verb and the pipe. Any thread.</summary>
public static void Execute(AutomationCommand command) => _coalescer?.Submit(command);

private static void RunCommand(AutomationCommand command)
{
    var pointer = CursorPoint();
    var paths = command.Paths;
    switch (command.Kind)
    {
        case CommandKind.Wheel:
            WheelHost?.OpenKeyboardWheel(paths);
            break;

        case CommandKind.Shelf:
            if (Shelves is { } shelves && !shelves.AddToRecentShelf(paths)) shelves.NewShelf(paths, pointer);
            break;

        case CommandKind.Convert:
        {
            var format = command.Format!.Value;
            var entry = Catalog?.Convert(paths).Entries.FirstOrDefault(e => e.Id == "convert." + format.Id());
            if (entry is null) { Toasts?.Show($"Can't convert these to {format.Title()}", pointer); break; }
            _ = entry.Run(new WheelRun(paths, pointer, OptionsRequested: false, SourceShelf: null));
            break;
        }
    }
}

/// <summary>A watched folder got new finished files: put them on a shelf, then run the folder's action.</summary>
private static async void OnFolderArrival(FolderArrival arrival)
{
    try
    {
        var pointer = CursorPoint();
        var files = arrival.Files;
        if (Shelves is { } shelves && !shelves.AddToRecentShelf(files)) shelves.NewShelf(files, pointer);

        if (arrival.Action is not { Length: > 0 } action || Catalog is null) return;
        if (action.StartsWith("convert.", StringComparison.Ordinal))
        {
            var format = OutputFormats.FromId(action["convert.".Length..]);
            if (format is null) return;
            // Only the files this format applies to (SPEC 3.1).
            var targets = files.Where(f => Catalog.Convert([f]).Entries.Any(e => e.Id == action)).ToList();
            if (targets.Count > 0)
                await Catalog.RunAsync(format.Value, new WheelRun(targets, pointer, false, null));
        }
        else if (action.StartsWith("tool.", StringComparison.Ordinal))
        {
            foreach (var file in files)
            {
                var entry = Catalog.Tools([file]).Entries.FirstOrDefault(e => e.Id == action);
                if (entry is not null) await entry.Run(new WheelRun([file], pointer, false, null));
            }
        }
    }
    catch { /* a failing action must not stop watching */ }
}

private static void SyncExplorerEntry()
{
    try
    {
        var exe = Environment.ProcessPath;
        if (exe is null || Preferences is null) return;
        var wanted = Preferences.GetJson("explorerEntry") == "true";
        if (wanted) { if (!ExplorerIntegration.IsRegistered(exe)) ExplorerIntegration.Register(exe); }
        else if (ExplorerIntegration.IsRegistered()) ExplorerIntegration.Unregister();
    }
    catch { /* registry access denied: leave it as it was */ }
}

/// <summary>For the Settings toggle ("Add Open with OpenShelf to the Explorer menu").</summary>
public static void SetExplorerEntry(bool on)
{
    Preferences?.SetJson("explorerEntry", on ? "true" : "false");
    SyncExplorerEntry();
}
```

`ExplorerIntegration.Register(exe)` should also be called by the installer (per-user, HKCU, no elevation).
Tool results are not yet reported through `Saved`, so a watched-folder tool action (Compress, Remove Background...)
writes a file next to the original that would arrive again and re-run the action. Before enabling tool actions, have
the Tools project raise a saved-files event like `ConversionWheelCatalog.Saved` and wire it to
`_watched.NoteOutputs`. Convert actions are safe already.

## 4. Settings

A toggle in Settings (General) calls `AppHost.SetExplorerEntry`. The Settings project must not reference
Automation, so pass it through `AppCommands` or a delegate on `SettingsWindowService`. Watched-folder editing
needs no change: `WatchedFolderService.ForPreferences` re-reads the `watchedFolders` key on every `Changed` event
and shows unavailable folders via `Unavailable` / `AvailabilityChanged` (the pane can show "Folder not found").

## Threading notes

- `onArrival`, `AvailabilityChanged` and the `CommandCoalescer` sink run on thread-pool/timer threads; the code above marshals to the dispatcher.
- Explorer starts one `OpenShelf.exe` per selected file (classic verb, `MultiSelectModel=Document`, max 15 files).
  Each forwards over the pipe; `CommandCoalescer` merges commands of the same kind within a 400 ms quiet window
  (3 s cap), so one wheel opens for all files.

## Modern Windows 11 menu

The entry above is a classic verb: Windows 11 shows it under "Show more options". A first-level entry needs an
`IExplorerCommand` COM handler packaged in a sparse MSIX (`windows.fileExplorerContextMenus` extension, package identity
via `Add-AppxPackage -ExternalLocation`, a signed package) plus a registered COM server (C++/WinRT or C# with
`GeneratedComInterface`). That is a separate installer task.
