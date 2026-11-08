using System.Windows;

namespace TaskbarPlayer;

public partial class App : Application
{
    static Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, "TaskbarPlayer.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }

        base.OnStartup(e);
        // Окно создаётся, но показывается только когда в системе появляется медиа-сессия.
        MainWindow = new MainWindow();
    }
}
