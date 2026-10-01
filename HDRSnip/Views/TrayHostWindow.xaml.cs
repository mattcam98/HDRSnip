using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using HDRSnip.Capture;
using HDRSnip.Models;
using HDRSnip.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace HDRSnip.Views;

/// <summary>
/// Invisible host for the tray icon and the global hotkeys, and the coordinator
/// for the whole capture flow. Nothing here touches DXGI directly.
/// </summary>
public partial class TrayHostWindow : Window
{
    /// <summary>Time for our own chrome to leave the screen before the grab.</summary>
    private static readonly TimeSpan ChromeSettleDelay = TimeSpan.FromMilliseconds(60);

    private readonly AppConfig _config;
    private readonly CaptureService _capture;
    private readonly HotkeyService _hotkeys;
    private EditorWindow? _editor;
    private SettingsWindow? _settings;
    private CaptureResult? _lastCapture;
    private bool _busy;

    /// <summary>System.Drawing.Icon reads lazily from its stream, so it must outlive construction.</summary>
    private MemoryStream? _iconStream;

    public TrayHostWindow()
    {
        InitializeComponent();
        _config = App.Config;
        _capture = new CaptureService(_config);
        _hotkeys = new HotkeyService(this);

        ApplyTrayIcon();
        NotificationService.OpenEditorRequested += OpenLastInEditor;
        SourceInitialized += (_, _) => RegisterHotkeys();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        NotificationService.OpenEditorRequested -= OpenLastInEditor;
        _hotkeys.Dispose();
        Tray.Dispose();
        _iconStream?.Dispose();
    }

    // ------------------------------------------------------------ tray surface

    private void ApplyTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is not null)
            {
                _iconStream = new MemoryStream();
                using (var stream = resource.Stream)
                    stream.CopyTo(_iconStream);
                _iconStream.Position = 0;
                Tray.Icon = new Icon(_iconStream);
                return;
            }
        }
        catch (Exception ex)
        {
            App.LogError("TrayIcon", ex);
        }

        try { Tray.Icon = SystemIcons.Application; }
        catch { /* the tray can live without an icon */ }
    }

    private void RegisterHotkeys()
    {
        _hotkeys.UnregisterAll();
        _hotkeys.TryRegister(_config.RegionHotkey, () => Run(() => CaptureRegionAsync(TimeSpan.Zero)));
        _hotkeys.TryRegister(_config.FullScreenHotkey, () => Run(() => CaptureFullScreenAsync(TimeSpan.Zero)));

        RegionItem.InputGestureText = _config.RegionHotkey.ToString();
        FullScreenItem.InputGestureText = _config.FullScreenHotkey.ToString();

        Tray.ToolTipText = _hotkeys.Conflicts.Count == 0
            ? $"HDRSnip\n{_config.RegionHotkey}  region\n{_config.FullScreenHotkey}  full screen"
            : "HDRSnip\nSome hotkeys are in use by another app — use the tray menu or change them in Settings.";
    }

    // ------------------------------------------------------------ tray commands

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => StartFromMenu();

    private void OnNewSnip(object sender, RoutedEventArgs e) => StartFromMenu();

    private void OnFullScreen(object sender, RoutedEventArgs e) =>
        Run(() => CaptureFullScreenAsync(ChromeSettleDelay));

    private void OnOpenLast(object sender, RoutedEventArgs e) => OpenLastInEditor();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_config.SaveFolder);
            Process.Start(new ProcessStartInfo(_config.SaveFolder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            App.LogError("OpenFolder", ex);
        }
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        // Released while Settings is open: a registered hotkey never reaches the
        // recorder as a key press, it would just start a capture over the dialog.
        _hotkeys.UnregisterAll();
        _settings = new SettingsWindow(_config);
        try
        {
            _settings.ShowDialog();
        }
        finally
        {
            _settings = null;
            RegisterHotkeys();
        }
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        // Shutdown ignores a cancelled close, so give editors holding unexported
        // markup their say first.
        foreach (var editor in Application.Current.Windows.OfType<EditorWindow>().ToList())
            editor.Close();

        if (!Application.Current.Windows.OfType<EditorWindow>().Any())
            Application.Current.Shutdown();
    }

    // ------------------------------------------------------------ capture flow

    private void StartFromMenu() => Run(async () =>
    {
        var bar = new ModeBarWindow();
        bar.ShowDialog();
        if (bar.ChosenMode is not { } mode)
            return;

        if (bar.DelaySeconds > 0)
            await CountdownWindow.RunAsync(bar.DelaySeconds);

        if (mode == SnipMode.FullScreen)
            await CaptureFullScreenAsync(ChromeSettleDelay);
        else
            await CaptureRegionAsync(ChromeSettleDelay);
    });

    /// <summary>Single entry point for capture work: one at a time, never fatal.</summary>
    private async void Run(Func<Task> work)
    {
        if (_busy)
            return;

        _busy = true;
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            App.LogError("Capture", ex);
            MessageBox.Show($"Capture failed.\n\n{ex.Message}", "HDRSnip",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;

            App.TrimMemoryWhenIdle();
        }
    }

    private async Task CaptureFullScreenAsync(TimeSpan settle)
    {
        if (settle > TimeSpan.Zero)
            await Task.Delay(settle);

        Present(await Task.Run(_capture.CaptureFullScreenAtCursor));
    }

    private async Task CaptureRegionAsync(TimeSpan settle)
    {
        if (settle > TimeSpan.Zero)
            await Task.Delay(settle);

        // Grab and tone-map off the UI thread so the tray app never looks hung.
        var (frame, preview, windows) = await Task.Run(() =>
        {
            var captured = CaptureService.GrabMonitorAtCursor();
            return (captured, _capture.RenderPreview(captured), Interop.Native.GetWindowBounds());
        });

        var overlay = new CaptureOverlayWindow(frame, preview, windows);
        overlay.ShowDialog();
        if (overlay.Selection is not { } selection)
            return;

        Present(await Task.Run(() => _capture.CropAndFinish(frame, selection)));
    }

    private void Present(CaptureResult result)
    {
        _lastCapture = result;
        OpenLastItem.IsEnabled = true;

        bool copied = _config.CopyToClipboard && ClipboardService.TryCopy(result.Image, result.Png);

        if (_config.OpenEditorAfterCapture)
        {
            OpenLastInEditor();
            return;
        }

        try
        {
            NotificationService.ShowCapture(result, copied);
        }
        catch (Exception ex)
        {
            // Toasts need a registered AUMID, which unpackaged side-loads may lack.
            App.LogError("Toast", ex);
            try
            {
                Tray.ShowBalloonTip("HDRSnip", NotificationService.Headline(result, copied) + ".",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
            catch { /* nothing more to offer */ }
        }
    }

    private void OpenLastInEditor()
    {
        if (_lastCapture is null)
            return;

        // Reuse the window unless it holds markup the user has not exported yet;
        // then a second window is far better than silently discarding their work.
        if (_editor is { IsLoaded: true } && !_editor.HasUnsavedEdits)
        {
            _editor.Load(_lastCapture);
            _editor.Activate();
            return;
        }

        var editor = new EditorWindow(_lastCapture, _capture);
        editor.Closed += (_, _) =>
        {
            // An older window closing must not forget a newer one.
            if (ReferenceEquals(_editor, editor))
                _editor = null;
        };
        _editor = editor;
        editor.Show();
        editor.Activate();
    }
}
