using System.Threading;
using System.Windows;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip;

public partial class App : System.Windows.Application
{
    public static AppConfig Config { get; private set; } = null!;
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, @"Local\HDRSnip.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("HDRSnip is already running (check the system tray).", "HDRSnip",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ToastNotificationService.Initialize();

        Config = AppConfig.Load();
        var host = new MainWindow();
        MainWindow = host;
        host.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
