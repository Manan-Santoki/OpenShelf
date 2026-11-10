using System.IO;
using System.IO.Pipes;
using System.Windows;

namespace OpenShelf.App;

/// <summary>
/// Entry point. Keeps one copy running: a second launch tells the first to show
/// Settings (the tray icon may be hidden in the overflow area) and exits.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "OpenShelf.SingleInstance";
    private const string PipeName = "OpenShelf.Activate";
    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCancel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(initiallyOwned: true, InstanceName, out var first);
        if (!first)
        {
            SignalRunningCopy();
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _pipeCancel?.Cancel();
        AppHost.Stop();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void SignalRunningCopy()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(800);
            client.WriteByte(1);
        }
        catch
        {
            // The first copy is still starting or shutting down; nothing to do.
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
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    _ = Dispatcher.BeginInvoke(AppHost.ShowSettingsFromSecondLaunch);
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
