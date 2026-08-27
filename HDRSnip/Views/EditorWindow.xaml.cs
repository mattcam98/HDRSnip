using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Services;
using Microsoft.Win32;
using DataObject = System.Windows.DataObject;

namespace HDRSnip.Views;

/// <summary>
/// Post-capture review: check the result, zoom in, then copy or save. It stays
/// open and reloads in place when a new capture arrives, so repeated snips do
/// not scatter windows across the desktop.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly double[] ZoomStops = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4];

    private readonly CaptureService _capture;
    private CaptureResult _result;
    private string? _savedPath;
    private double? _zoom;   // null means fit to viewport

    public EditorWindow(CaptureResult result, CaptureService capture)
    {
        InitializeComponent();
        _capture = capture;
        _result = result;

        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);
        Load(result);
    }

    /// <summary>Swaps in a newer capture without recreating the window.</summary>
    public void Load(CaptureResult result)
    {
        _result = result;
        _savedPath = result.SavedPath;
        Preview.Source = result.Image;

        HdrBadge.Visibility = result.WasHdr ? Visibility.Visible : Visibility.Collapsed;
        Dimensions.Text = $"{result.Image.PixelWidth} × {result.Image.PixelHeight}";
        _zoom = null;
        ApplyZoom();

        Status.Text = string.IsNullOrEmpty(_savedPath)
            ? "Copied to clipboard"
            : $"Saved to {_savedPath}";
    }

    // ------------------------------------------------------------ commands

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var data = new DataObject();
        data.SetImage(_result.Image);

        try
        {
            var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(_result.Image));
            encoder.Save(png);
            png.Position = 0;
            data.SetData("PNG", png, false);
        }
        catch (Exception ex)
        {
            // The DIB alone still pastes everywhere that matters.
            App.LogError("EditorCopyPng", ex);
        }

        try
        {
            Clipboard.SetDataObject(data, copy: true);
            Status.Text = "Copied to clipboard";
        }
        catch (Exception ex)
        {
            App.LogError("EditorCopy", ex);
            Status.Text = "Another app is holding the clipboard — try again";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveTo(_savedPath ?? _capture.BuildSavePath());

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var suggested = _savedPath ?? _capture.BuildSavePath();
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested)
        };

        if (dialog.ShowDialog(this) == true)
            SaveTo(dialog.FileName);
    }

    private void SaveTo(string path)
    {
        try
        {
            CaptureService.SavePng(_result.Image, path);
            _savedPath = path;
            Status.Text = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            App.LogError("EditorSave", ex);
            Status.Text = $"Could not save: {ex.Message}";
        }
    }

    // ------------------------------------------------------------ zoom

    private void OnToggleZoom(object sender, RoutedEventArgs e)
    {
        _zoom = _zoom is null ? 1 : null;
        ApplyZoom();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => StepZoom(1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        StepZoom(Math.Sign(e.Delta));
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoom is null)
            ApplyZoom();
    }

    private void StepZoom(int direction)
    {
        double current = _zoom ?? FitScale();
        int index = Array.FindIndex(ZoomStops, stop => stop > current + 0.001);
        if (direction < 0)
            index = Array.FindLastIndex(ZoomStops, stop => stop < current - 0.001);

        if (index < 0)
            index = direction > 0 ? ZoomStops.Length - 1 : 0;

        _zoom = ZoomStops[index];
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_result is null)
            return;

        double scale = _zoom ?? FitScale();
        Preview.Width = _result.Image.PixelWidth * scale;
        Preview.Height = _result.Image.PixelHeight * scale;
        ZoomLabel.Text = _zoom is null ? "Fit" : $"{Math.Round(scale * 100)}%";
    }

    /// <summary>Largest scale that fits the viewport, never enlarging past 1:1.</summary>
    private double FitScale()
    {
        const double padding = 56 + 2;
        double available = Math.Max(Viewport.ActualWidth - padding, 1);
        double availableHeight = Math.Max(Viewport.ActualHeight - padding, 1);

        return Math.Min(1.0, Math.Min(
            available / _result.Image.PixelWidth,
            availableHeight / _result.Image.PixelHeight));
    }

    // ------------------------------------------------------------ keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (e.Key)
        {
            case Key.C when control:
                OnCopy(sender, e);
                break;
            case Key.S when control && shift:
                OnSaveAs(sender, e);
                break;
            case Key.S when control:
                OnSave(sender, e);
                break;
            case Key.D0 or Key.NumPad0 when control:
                OnToggleZoom(sender, e);
                break;
            case Key.OemPlus or Key.Add when control:
                StepZoom(1);
                break;
            case Key.OemMinus or Key.Subtract when control:
                StepZoom(-1);
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
