using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Services;
using Microsoft.Win32;

namespace HDRSnip.Views;

/// <summary>
/// A capture floated above every other window, for keeping a reference in view
/// while working. Drag moves it, the wheel resizes it.
/// </summary>
public partial class PinWindow : Window
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 4;

    private readonly BitmapSource _image;
    private double _zoom = 1;   // screen pixels per image pixel

    public PinWindow(BitmapSource image)
    {
        InitializeComponent();
        _image = image;
        Picture.Source = image;

        // Start at actual size unless that would cover most of the screen.
        var work = SystemParameters.WorkArea;
        _zoom = Math.Min(1, Math.Min(work.Width * 0.6 / image.PixelWidth, work.Height * 0.6 / image.PixelHeight));
        ApplyZoom();

        SourceInitialized += (_, _) => ApplyZoom();
        DpiChanged += (_, _) => ApplyZoom();
        Closed += (_, _) => App.TrimMemoryWhenIdle();
    }

    private void ApplyZoom()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Picture.Width = Math.Max(16, _image.PixelWidth * _zoom / dpi);
        Picture.Height = Math.Max(16, _image.PixelHeight * _zoom / dpi);

        // Crisp when magnified, smooth when shrunk.
        RenderOptions.SetBitmapScalingMode(Picture,
            _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            Close();
        else
            DragMove();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (e.Key == Key.Escape)
            Close();
        else if (e.Key == Key.C && control)
            OnCopy(sender, e);
        else if (e.Key is Key.D0 or Key.NumPad0 && control)
            OnActualSize(sender, e);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => ClipboardService.TryCopy(_image);

    private void OnActualSize(object sender, RoutedEventArgs e)
    {
        _zoom = 1;
        ApplyZoom();
    }

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            FileName = $"HDRSnip_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = App.Config.SaveFolder
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ImageCodec.Save(_image, dialog.FileName);
        }
        catch (Exception ex)
        {
            App.LogError("PinSave", ex);
            MessageBox.Show(this, $"Could not save.\n\n{ex.Message}", "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
