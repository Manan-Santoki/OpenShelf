using System.IO;
using System.IO.Pipes;
using System.Windows;
using OpenShelf.Automation;

namespace OpenShelf.App;

/// <summary>
/// Entry point. Keeps one copy running: a second launch tells the first to show
/// Settings (the tray icon may be hidden in the overflow area) and exits.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "OpenShelf.SingleInstance";
    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCancel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        OpenShelf.Ui.AppIcons.RegisterForAllWindows();   // the logo in every window's title bar, taskbar button and Alt+Tab entry

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

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            args.Handled = true;
        };

        AppHost.Start(this);
        ListenForSecondLaunch();
        if (parsed.Command is { } command) AppHost.Execute(command);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancel?.Cancel();
        AppHost.Stop();
        _mutex?.Dispose();
        base.OnExit(e);
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
        catch
        {
            // The first copy is shutting down; nothing to do.
        }
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
                        case CommandRequest c: AppHost.Execute(c.Command); break;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log(ex);
                    await Task.Delay(500, CancellationToken.None);
                }
            }
        }, token);
    }

    private static void Log(Exception exception)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShelf", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "openshelf.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [app] unhandled: {exception}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
