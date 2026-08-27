using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Models;
using HDRSnip.Services;
using Application = System.Windows.Application;
using DataObject = System.Windows.DataObject;
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
    private HotkeyService? _hotkeys;
    private EditorWindow? _editor;
    private CaptureResult? _lastCapture;
    private bool _busy;

    /// <summary>System.Drawing.Icon reads lazily from its stream, so it must outlive construction.</summary>
    private MemoryStream? _iconStream;

    public TrayHostWindow()
    {
        InitializeComponent();
        _config = App.Config;
        _capture = new CaptureService(_config);

        ApplyTrayIcon();
        NotificationService.OpenEditorRequested += OpenLastInEditor;
        SourceInitialized += (_, _) => RegisterHotkeys();
        Closed += OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        NotificationService.OpenEditorRequested -= OpenLastInEditor;
        _hotkeys?.Dispose();
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
        _hotkeys?.Dispose();
        _hotkeys = new HotkeyService(this);

        _hotkeys.TryRegister(_config.RegionHotkey, () => Run(() => CaptureRegionAsync(TimeSpan.Zero)));
        _hotkeys.TryRegister(_config.FullScreenHotkey, () => Run(() => CaptureFullScreenAsync(TimeSpan.Zero)));

        RegionItem.InputGestureText = _config.RegionHotkey.ToString();
        FullScreenItem.InputGestureText = _config.FullScreenHotkey.ToString();

        Tray.ToolTipText = _hotkeys.Conflicts.Count == 0
            ? $"HDRSnip\n{_config.RegionHotkey}  region\n{_config.FullScreenHotkey}  full screen"
            : $"HDRSnip\nSome hotkeys are in use by another app — use the tray menu or change them in Settings.";
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
        var settings = new SettingsWindow(_config);
        if (settings.ShowDialog() == true)
            RegisterHotkeys();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        _hotkeys?.Dispose();
        Application.Current.Shutdown();
    }

    // ------------------------------------------------------------ capture flow

    private void StartFromMenu() => Run(async () =>
    {
        var bar = new ModeBarWindow();
        bar.ShowDialog();
        if (bar.ChosenMode is not { } mode)
            return;

        if (mode == SnipMode.FullScreen)
            await CaptureFullScreenAsync(ChromeSettleDelay);
        else
            await CaptureRegionAsync(ChromeSettleDelay);
    });

    /// <summary>Single entry point for capture work: one at a time, never fatal.</summary>
    private void Run(Func<Task> work)
    {
        if (_busy)
            return;

        _busy = true;
        _ = Execute();
        return;

        async Task Execute()
        {
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
            }
        }
    }

    private async Task CaptureFullScreenAsync(TimeSpan settle)
    {
        if (settle > TimeSpan.Zero)
            await Task.Delay(settle);

        var result = await Task.Run(_capture.CaptureFullScreenAtCursor);
        Present(result);
    }

    private async Task CaptureRegionAsync(TimeSpan settle)
    {
        if (settle > TimeSpan.Zero)
            await Task.Delay(settle);

        // Grab and tone-map off the UI thread so the tray app never looks hung.
        var (frame, preview) = await Task.Run(() =>
        {
            var captured = _capture.GrabMonitorAtCursor();
            return (captured, _capture.RenderPreview(captured));
        });

        var overlay = new CaptureOverlayWindow(frame, preview);
        overlay.ShowDialog();
        if (overlay.Selection is not { } selection)
            return;

        var result = await Task.Run(() => _capture.CropAndFinish(frame, selection));
        Present(result);
    }

    private void Present(CaptureResult result)
    {
        _lastCapture = result;
        OpenLastItem.IsEnabled = true;

        if (_config.CopyToClipboard)
            CopyToClipboard(result.Image);

        if (_config.OpenEditorAfterCapture)
        {
            OpenLastInEditor();
            return;
        }

        try
        {
            NotificationService.ShowCaptureCopied(
                result.WasHdr,
                result.Image.PixelWidth,
                result.Image.PixelHeight,
                NotificationService.TryWritePreview(result.Image));
        }
        catch (Exception ex)
        {
            // Toasts need a registered AUMID, which unpackaged side-loads may lack.
            App.LogError("Toast", ex);
            try
            {
                Tray.ShowBalloonTip("HDRSnip",
                    result.WasHdr ? "HDR screenshot copied." : "Screenshot copied.",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
            catch { /* nothing more to offer */ }
        }
    }

    /// <summary>
    /// Offers PNG alongside the DIB so apps that prefer it keep exact pixels and
    /// an alpha channel. Retries because the clipboard is a shared, lockable resource.
    /// </summary>
    private static void CopyToClipboard(BitmapSource image)
    {
        MemoryStream? png = null;
        try
        {
            png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(png);
        }
        catch (Exception ex)
        {
            App.LogError("ClipboardPng", ex);
            png = null;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = new DataObject();
                data.SetImage(image);
                if (png is not null)
                {
                    png.Position = 0;
                    data.SetData("PNG", png, false);
                }

                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (Exception ex)
            {
                App.LogError($"Clipboard#{attempt}", ex);
                Thread.Sleep(40);
            }
        }
    }

    private void OpenLastInEditor()
    {
        if (_lastCapture is null)
            return;

        if (_editor is { IsLoaded: true })
        {
            _editor.Load(_lastCapture);
            _editor.Activate();
            return;
        }

        _editor = new EditorWindow(_lastCapture, _capture);
        _editor.Closed += (_, _) => _editor = null;
        _editor.Show();
        _editor.Activate();
    }
}
