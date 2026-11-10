using System.IO;
using System.Windows;
using OpenShelf.Core;
using OpenShelf.Update;

namespace OpenShelf.App;

/// <summary>Connects <see cref="UpdateService"/> to the app: toasts for messages, a message box for the two questions.</summary>
internal static class UpdateWiring
{
    public static UpdateService Create(IPreferences preferences, IToastService toasts, Func<ScreenPoint> pointer, Action beforeRestart)
    {
        var service = new UpdateService(new VelopackBackend(), () => preferences.AutomaticUpdates)
        {
            OnError = ex => Log($"background check failed: {ex.Message}"),
        };

        service.Prompts.Notify = message => OnUi(() => toasts.Show(message, pointer()));
        service.Prompts.ConfirmInstall = version => AskAsync(
            $"OpenShelf {version} is available (you have {service.CurrentVersion}).\n\nDownload it and restart OpenShelf now?",
            "Update available",
            then: () => toasts.Show($"Downloading OpenShelf {version}…", pointer()));
        service.Prompts.ConfirmRestart = version => AskAsync(
            $"OpenShelf {version} has been downloaded.\n\nRestart OpenShelf to finish updating? If you choose No, it installs the next time OpenShelf starts.",
            "Update ready");
        service.Prompts.BeforeRestart = beforeRestart;
        return service;
    }

    private static Task<bool> AskAsync(string text, string title, Action? then = null)
    {
        var done = new TaskCompletionSource<bool>();
        OnUi(() =>
        {
            var yes = MessageBox.Show(text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (yes) then?.Invoke();
            done.SetResult(yes);
        });
        return done.Task;
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static void Log(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenShelf", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "openshelf.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [update] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
