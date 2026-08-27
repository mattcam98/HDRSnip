using System.Diagnostics;
using System.IO;
using System.Windows;
using HDRSnip.Capture;
using HDRSnip.Models;
using HDRSnip.Services;
using HDRSnip.Views;

namespace HDRSnip;

public partial class App : Application
{
    private const string SingleInstanceMutex = @"Local\HDRSnip.SingleInstance";

    public static AppConfig Config { get; private set; } = null!;

    private Mutex? _instanceLock;
    private bool _ownsInstanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        // The capture daemon is this same executable relaunched with a flag. It
        // must short-circuit before any WPF or single-instance machinery runs.
        if (CaptureDaemon.TryRun(args))
            return;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallErrorHandlers();
        base.OnStartup(e);

        _instanceLock = new Mutex(true, SingleInstanceMutex, out _ownsInstanceLock);
        if (!_ownsInstanceLock)
        {
            MessageBox.Show(
                "HDRSnip is already running — look for it in the system tray.",
                "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Config = AppConfig.Load();
        ThemeService.Initialize();
        NotificationService.Initialize();

        // Warm the capture daemon while the tray icon is being built.
        CaptureHost.Start();

        var tray = new TrayHostWindow();
        MainWindow = tray;
        tray.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CaptureHost.Stop();
        NotificationService.Shutdown();
        ThemeService.Shutdown();

        try
        {
            if (_ownsInstanceLock)
                _instanceLock?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned on this thread — nothing to release.
        }

        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    private void InstallErrorHandlers()
    {
        // A tray utility should survive a bad frame, not vanish from the notification area.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("Dispatcher", args.Exception);
            args.Handled = true;
            ShowError(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogError("Unhandled", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError("Task", args.Exception);
            args.SetObserved();
        };
    }

    private static void ShowError(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"HDRSnip hit an error but is still running.\n\n{ex.Message}",
                "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { /* the UI itself is unhappy; the log already has it */ }
    }

    /// <summary>Appends to %LOCALAPPDATA%\HDRSnip\errors.log. Never throws.</summary>
    public static void LogError(string source, Exception ex)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDRSnip");
            Directory.CreateDirectory(directory);

            var entry = $"[{DateTime.Now:o}] {source}: {ex}\n";
            File.AppendAllText(Path.Combine(directory, "errors.log"), entry);
            Debug.WriteLine(entry);
        }
        catch { /* logging must never be the thing that breaks */ }
    }
}
