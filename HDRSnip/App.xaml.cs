using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    /// <summary>Where images dragged out of the editor are staged. Emptied at startup and exit.</summary>
    public static string DragOutFolder { get; } = Path.Combine(Path.GetTempPath(), "HDRSnip", "drag");

    private static void ClearDragOutFolder()
    {
        try
        {
            if (Directory.Exists(DragOutFolder))
                Directory.Delete(DragOutFolder, recursive: true);
        }
        catch (Exception ex)
        {
            LogError("DragOutCleanup", ex);
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        InstallErrorHandlers();
        base.OnStartup(e);
        ClearDragOutFolder();

        // `HDRSnip --edit image.png` opens the markup editor on an existing file.
        // It runs beside a tray instance rather than replacing it.
        if (e.Args is ["--edit", var imagePath])
        {
            OpenStandaloneEditor(imagePath);
            return;
        }

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

        try
        {
            await AutostartService.ReconcileAsync(Config.StartWithWindows);
        }
        catch (Exception ex)
        {
            LogError("Autostart", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ClearDragOutFolder();
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

    private void OpenStandaloneEditor(string imagePath)
    {
        Config = AppConfig.Load();
        ThemeService.Initialize();
        ShutdownMode = ShutdownMode.OnLastWindowClose;

        try
        {
            var fullPath = Path.GetFullPath(imagePath);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(fullPath);
            image.EndInit();
            image.Freeze();

            var editor = new EditorWindow(
                new CaptureResult(image, WasHdr: false, SavedPath: fullPath),
                new CaptureService(Config));
            editor.ShowStatus($"Opened {fullPath}");
            MainWindow = editor;
            editor.Show();
        }
        catch (Exception ex)
        {
            LogError("StandaloneEditor", ex);
            MessageBox.Show($"Could not open {imagePath}.\n\n{ex.Message}", "HDRSnip",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
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

    /// <summary>
    /// Returns memory once the UI goes quiet. Captures and editing leave
    /// frame-sized buffers and discarded bitmaps behind, and the app then idles in
    /// the tray: nothing else would prompt their release. WPF bitmaps free their
    /// pixels in finalizers, hence the second collection.
    /// </summary>
    public static void TrimMemoryWhenIdle() =>
        Current?.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        });

    /// <summary>
    /// Appends to %LOCALAPPDATA%\HDRSnip\errors.log. Never throws, and needs no
    /// <see cref="App"/> instance, so the capture daemon uses it too.
    /// </summary>
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
