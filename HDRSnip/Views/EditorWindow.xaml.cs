using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using Microsoft.Win32;

namespace HDRSnip.Views;

public partial class EditorWindow : Window
{
    private readonly BitmapSource _image;
    private readonly ScreenCaptureService _capture;
    private string? _savedPath;

    public EditorWindow(BitmapSource image, bool wasHdr, ScreenCaptureService capture, string? savedPath)
    {
        InitializeComponent();
        _image = image;
        _capture = capture;
        _savedPath = savedPath;
        Preview.Source = image;
        HdrBadge.Visibility = wasHdr ? Visibility.Visible : Visibility.Collapsed;
        HdrBadge.Text = wasHdr ? "HDR → SDR" : "SDR";
        StatusText.Text = savedPath is not null
            ? $"Saved · {savedPath}"
            : $"{image.PixelWidth}×{image.PixelHeight} · copied to clipboard";
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Clipboard.SetImage(_image);
        StatusText.Text = "Copied to clipboard";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _savedPath ??= _capture.BuildSavePath();
        ScreenCaptureService.SavePng(_image, _savedPath);
        StatusText.Text = $"Saved · {_savedPath}";
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            FileName = Path.GetFileName(_savedPath ?? _capture.BuildSavePath()),
            InitialDirectory = Path.GetDirectoryName(_savedPath ?? _capture.BuildSavePath())
        };
        if (dlg.ShowDialog(this) == true)
        {
            _savedPath = dlg.FileName;
            ScreenCaptureService.SavePng(_image, _savedPath);
            StatusText.Text = $"Saved · {_savedPath}";
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
            OnCopy(sender, e);
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
            OnSave(sender, e);
        else if (e.Key == Key.Escape)
            Close();
    }
}
