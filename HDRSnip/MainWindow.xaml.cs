using System.Drawing;
using System.Windows;
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

    public MainWindow()
    {
        InitializeComponent();
        _config = App.Config;
        _capture = new ScreenCaptureService(_config);

        TrySetTrayIcon();
        SourceInitialized += (_, _) => RegisterHotkeys();
        Closed += (_, _) =>
        {
            _hotkeys?.Dispose();
            Tray.Dispose();
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
                Tray.Icon = new Icon(stream);
                return;
            }
        }
        catch
        {
            // fall through
        }

        // Fallback: system screenshot-ish icon via shell32
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
            _hotkeys.Register(_config.RegionHotkeyModifiers, _config.RegionHotkeyVk, () => _ = StartSnipAsync(showToolbar: false));
            _hotkeys.Register(_config.FullScreenHotkeyModifiers, _config.FullScreenHotkeyVk, () => _ = CaptureFullAsync());
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

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e) => _ = StartSnipAsync(showToolbar: true);
    private void OnNewSnip(object sender, RoutedEventArgs e) => _ = StartSnipAsync(showToolbar: true);
    private void OnFullSnip(object sender, RoutedEventArgs e) => _ = CaptureFullAsync();

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
        catch (Exception ex)
        {
            MessageBox.Show($"Capture failed:\n{ex.Message}", "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Error);
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
        catch (Exception ex)
        {
            MessageBox.Show($"Capture failed:\n{ex.Message}", "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private Task CaptureFullAsyncCore()
    {
        // Yield so any UI (toolbar) can close before DXGI grab.
        return Dispatcher.InvokeAsync(() =>
        {
            var result = _capture.CaptureFullScreenAtCursor();
            Present(result);
        }).Task;
    }

    private async Task CaptureRegionAsync()
    {
        await Dispatcher.InvokeAsync(() => { }).Task;
        await Task.Delay(80); // let toolbar close / desktop settle

        var (frame, preview) = _capture.CaptureFrozenMonitorAtCursor();
        try
        {
            var overlay = new CaptureOverlayWindow(frame, preview);
            overlay.ShowDialog();
            if (!overlay.Confirmed || overlay.Selection is null)
                return;

            var result = _capture.CropAndFinish(frame, overlay.Selection.Value);
            Present(result);
        }
        finally
        {
            frame.Dispose();
        }
    }

    private void Present(CaptureResult result)
    {
        if (_config.OpenEditorAfterCapture)
        {
            var editor = new EditorWindow(result.Image, result.WasHdr, _capture, result.SavedPath);
            editor.Show();
            editor.Activate();
        }
        else
        {
            Tray.ShowBalloonTip("HDRSnip",
                result.WasHdr ? "HDR screenshot copied (tone-mapped to SDR)." : "Screenshot copied.",
                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
        }
    }
}
