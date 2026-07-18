using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Models;
using HDRSnip.Services;
using HDRSnip.Views;

namespace HDRSnip;

public partial class MainWindow : Window
{
    private readonly AppConfig _config;
    private readonly ScreenCaptureService _capture;
    private HotkeyService? _hotkeys;
    private bool _busy;

    private BitmapSource? _lastImage;
    private bool _lastWasHdr;
    private string? _lastSavedPath;
    private EditorWindow? _editor;

    /// <summary>Must stay alive — Icon(Stream) requires the stream for the icon lifetime.</summary>
    private MemoryStream? _trayIconStream;

    public MainWindow()
    {
        InitializeComponent();
        _config = App.Config;
        _capture = new ScreenCaptureService(_config);

        TrySetTrayIcon();
        ToastNotificationService.OpenEditorRequested += OpenLastInEditor;
        SourceInitialized += (_, _) => RegisterHotkeys();
        Closed += (_, _) =>
        {
            ToastNotificationService.OpenEditorRequested -= OpenLastInEditor;
            _hotkeys?.Dispose();
            Tray.Dispose();
            _trayIconStream?.Dispose();
        };
    }

    private void TrySetTrayIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var streamInfo = Application.GetResourceStream(uri);
            if (streamInfo is not null)
            {
                using var stream = streamInfo.Stream;
                _trayIconStream = new MemoryStream();
                stream.CopyTo(_trayIconStream);
                _trayIconStream.Position = 0;
                Tray.Icon = new Icon(_trayIconStream);
                return;
            }
        }
        catch (Exception ex)
        {
            App.LogCrash("TrayIcon", ex);
        }

        try
        {
            Tray.Icon = SystemIcons.Application;
        }
        catch
        {
            // TaskbarIcon can run without icon in worst case
        }
    }

    private void RegisterHotkeys()
    {
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(this);
        try
        {
            _hotkeys.Register(_config.RegionHotkeyModifiers, _config.RegionHotkeyVk,
                () => _ = RunCaptureSafe(() => StartSnipAsync(showToolbar: false)));
            _hotkeys.Register(_config.FullScreenHotkeyModifiers, _config.FullScreenHotkeyVk,
                () => _ = RunCaptureSafe(CaptureFullAsync));
            Tray.ToolTipText =
                $"HDRSnip\n{HotkeyText.Format(_config.RegionHotkeyModifiers, _config.RegionHotkeyVk)} rectangular\n" +
                $"{HotkeyText.Format(_config.FullScreenHotkeyModifiers, _config.FullScreenHotkeyVk)} fullscreen";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not register one or more hotkeys:\n{ex.Message}\n\nUse the tray menu instead, or change hotkeys in Settings.",
                "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static async Task RunCaptureSafe(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            App.LogCrash("Capture", ex);
            try
            {
                MessageBox.Show($"Capture failed:\n{ex.Message}", "HDRSnip",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* ignore */ }
        }
    }

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) =>
        _ = RunCaptureSafe(() => StartSnipAsync(showToolbar: true));
    private void OnNewSnip(object sender, RoutedEventArgs e) =>
        _ = RunCaptureSafe(() => StartSnipAsync(showToolbar: true));
    private void OnFullSnip(object sender, RoutedEventArgs e) =>
        _ = RunCaptureSafe(CaptureFullAsync);

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_config) { Owner = this };
        if (dlg.ShowDialog() == true)
            RegisterHotkeys();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        _hotkeys?.Dispose();
        Application.Current.Shutdown();
    }

    private async Task StartSnipAsync(bool showToolbar)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            CaptureMode mode = CaptureMode.Rectangle;
            if (showToolbar)
            {
                var bar = new SnipToolbarWindow();
                bar.ShowDialog();
                if (bar.Cancelled || bar.ChosenMode is null)
                    return;
                mode = bar.ChosenMode.Value;
            }

            switch (mode)
            {
                case CaptureMode.FullScreen:
                case CaptureMode.Window:
                    await CaptureFullAsyncCore();
                    break;
                default:
                    await CaptureRegionAsync();
                    break;
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CaptureFullAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await CaptureFullAsyncCore();
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task CaptureFullAsyncCore()
    {
        await Task.Delay(50);
        var result = await Task.Run(() => _capture.CaptureFullScreenAtCursor()).ConfigureAwait(true);
        Present(result);
    }

    private async Task CaptureRegionAsync()
    {
        await Task.Delay(80);

        // DXGI + tone-map off the UI thread so the tray app never freezes / appears hung.
        var (frame, preview) = await Task.Run(() => _capture.CaptureFrozenMonitorAtCursor())
            .ConfigureAwait(true);
        try
        {
            var overlay = new CaptureOverlayWindow(frame, preview);
            overlay.ShowDialog();
            if (!overlay.Confirmed || overlay.Selection is null)
                return;

            var selection = overlay.Selection.Value;
            var result = await Task.Run(() => _capture.CropAndFinish(frame, selection))
                .ConfigureAwait(true);
            Present(result);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void Present(CaptureResult result)
    {
        _lastImage = result.Image;
        _lastWasHdr = result.WasHdr;
        _lastSavedPath = result.SavedPath;

        if (_config.CopyToClipboard)
            SafeSetClipboard(result.Image);

        if (_config.OpenEditorAfterCapture)
        {
            OpenLastInEditor();
            return;
        }

        try
        {
            var previewPath = ToastNotificationService.WriteTempPreview(result.Image);
            ToastNotificationService.ShowCaptureCopied(
                result.WasHdr,
                result.Image.PixelWidth,
                result.Image.PixelHeight,
                previewPath);
        }
        catch (Exception ex)
        {
            App.LogCrash("Toast", ex);
            try
            {
                Tray.ShowBalloonTip(
                    "HDRSnip",
                    result.WasHdr ? "HDR screenshot copied." : "Screenshot copied.",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
            catch { /* ignore */ }
        }
    }

    private static void SafeSetClipboard(BitmapSource image)
    {
        // Clipboard can be locked by other apps; never let that kill the process.
        for (int i = 0; i < 3; i++)
        {
            try
            {
                Clipboard.SetImage(image);
                return;
            }
            catch (Exception ex)
            {
                App.LogCrash($"Clipboard#{i}", ex);
                Thread.Sleep(40);
            }
        }
    }

    private void OpenLastInEditor()
    {
        if (_lastImage is null) return;

        if (_editor is { IsLoaded: true })
        {
            _editor.Activate();
            return;
        }

        _editor = new EditorWindow(_lastImage, _lastWasHdr, _capture, _lastSavedPath);
        _editor.Closed += (_, _) => _editor = null;
        _editor.Show();
        _editor.Activate();
    }
}
