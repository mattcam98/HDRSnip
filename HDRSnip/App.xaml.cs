using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using HDRSnip.Capture;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip;

public partial class App : System.Windows.Application
{
    public static AppConfig Config { get; private set; } = null!;
    private static Mutex? _mutex;
    private static bool _ownsMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        // Capture processes must run before WPF Application / tray mutex.
        if (CaptureDaemon.TryRun(args))
            return;
        if (CaptureWorker.TryRunAsWorker(args))
            return;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true;
            try
            {
                MessageBox.Show(
                    $"HDRSnip hit an error but stayed running:\n{args.Exception.Message}",
                    "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch { /* ignore */ }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogCrash("Unhandled", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("Task", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--export-store-assets", StringComparison.OrdinalIgnoreCase)))
        {
            Config = AppConfig.Load();
            var fromProject = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "store-listing"));
            try { Directory.CreateDirectory(fromProject); }
            catch { fromProject = Path.Combine(Path.GetTempPath(), "HDRSnip-store-listing"); Directory.CreateDirectory(fromProject); }

            Dispatcher.BeginInvoke(() =>
            {
                try { StoreAssetExporter.Export(fromProject); }
                catch (Exception ex) { MessageBox.Show(ex.ToString(), "Export failed"); }
                finally { Shutdown(); }
            });
            return;
        }

        _mutex = new Mutex(true, @"Local\HDRSnip.SingleInstance", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("HDRSnip is already running (check the system tray).", "HDRSnip",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ToastNotificationService.Initialize();

        Config = AppConfig.Load();
        CaptureHost.Start();
        var host = new MainWindow();
        MainWindow = host;
        host.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        CaptureHost.Stop();
        try
        {
            if (_ownsMutex)
                _mutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Mutex was not owned on this thread — ignore.
        }

        _mutex?.Dispose();
        base.OnExit(e);
    }

    internal static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRSnip");
            Directory.CreateDirectory(dir);
            var line = $"[{DateTime.Now:o}] {source}: {ex}\n";
            File.AppendAllText(Path.Combine(dir, "errors.log"), line);
            Debug.WriteLine(line);
        }
        catch { /* ignore */ }
    }
}
