using System.Windows;
using OpenShelf.Core;

namespace OpenShelf.Settings;

/// <summary>Owns the one settings window: creates it on first use, re-shows it afterwards, remembers the last tab.</summary>
public sealed class SettingsWindowService : ISettingsWindow
{
    public const string TabKey = "settings.tab";

    private readonly IPreferences _preferences;
    private readonly AppCommands _commands;
    private SettingsWindow? _window;
    private IToolInstallerUi? _installer;
    private readonly SettingsHooks _hooks = new();

    public SettingsWindowService(IPreferences preferences, AppCommands commands)
    {
        _preferences = preferences;
        _commands = commands;
    }

    /// <summary>The tool installer behind the Extra Formats tab; rows show "Coming soon" while this is null.</summary>
    public IToolInstallerUi? ToolInstaller
    {
        get => _installer;
        set
        {
            _installer = value;
            RunOnUi(() => { if (_window is not null) _window.Tools.Installer = value; });
        }
    }

    /// <summary>Whether the Explorer right-click entry is registered. The row is hidden while <see cref="SetExplorerEntry"/> is null.</summary>
    public Func<bool>? ExplorerEntryEnabled { get => _hooks.ExplorerEntryEnabled; set => _hooks.ExplorerEntryEnabled = value; }

    /// <summary>Registers or removes the Explorer right-click entry. May throw; the toggle then shows the real state.</summary>
    public Action<bool>? SetExplorerEntry { get => _hooks.SetExplorerEntry; set => _hooks.SetExplorerEntry = value; }

    /// <summary>Where to open the window instead of centring it (screenshots and tests).</summary>
    internal Point? FixedPosition { get; set; }

    internal Window? Window => _window;

    public void Show(string? tab = null) => RunOnUi(() => ShowCore(tab));

    private void ShowCore(string? tab)
    {
        var id = SettingsWindow.Normalize(tab) ?? SettingsWindow.Normalize(_preferences.GetJson(TabKey)) ?? "general";
        if (_window is null)
        {
            _window = new SettingsWindow(_preferences, _commands, saved => _preferences.SetJson(TabKey, saved), _hooks)
            {
                FixedPosition = FixedPosition,
            };
            _window.Tools.Installer = _installer;
            _window.Closed += (_, _) => _window = null;
            _window.SelectTab(id, animate: false);
            _window.Show();
        }
        else
        {
            _window.SelectTab(id, animate: true);
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            if (!_window.IsVisible) _window.Show();
        }
        // Bring it to the front even when Windows won't let a background app take focus.
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Activate();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }
}

/// <summary>Things Settings asks the app to do without referencing the project that does them.</summary>
internal sealed class SettingsHooks
{
    public Func<bool>? ExplorerEntryEnabled { get; set; }
    public Action<bool>? SetExplorerEntry { get; set; }
}
