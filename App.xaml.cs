using System.Windows;
using Bro.Core;

namespace Bro;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--uninstall"))
        {
            Uninstaller.Run(quiet: e.Args.Contains("--quiet"));
            Shutdown();
            return;
        }

        Services.Init();
        var (urls, active) = Services.Settings.Startup == "restore"
            ? Services.LoadSession()
            : (new List<string>(), 0);
        var window = new MainWindow(false, urls, active);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services.History != null) Services.Flush();
        base.OnExit(e);
    }
}
