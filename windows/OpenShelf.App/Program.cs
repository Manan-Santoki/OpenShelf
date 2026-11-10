using Velopack;

namespace OpenShelf.App;

/// <summary>
/// Custom entry point. Velopack runs the installer's hooks (first run, update, uninstall) through
/// <see cref="VelopackApp"/> and exits from inside <c>Run()</c>, so it must come first and before WPF starts.
/// A copy run from a build folder passes straight through.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
