using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using HDRSnip.Capture;
using Point = System.Windows.Point;

namespace HDRSnip.Views;

public partial class CaptureOverlayWindow : Window
{
    private readonly CapturedFrame _frame;
    private Point _start;
    private bool _dragging;

    public Int32Rect? Selection { get; private set; }
    public bool Confirmed { get; private set; }

    public CaptureOverlayWindow(CapturedFrame frame, BitmapSource preview)
    {
        _frame = frame;
        InitializeComponent();

        // DXGI bounds are physical pixels; WPF Left/Top/Width/Height are DIPs.
        var (left, top, width, height) = MonitorDpi.PhysicalToDip(frame.MonitorBounds);
        Left = left;
        Top = top;
        Width = width;
        Height = height;

        PreviewImage.Source = preview;
        Loaded += (_, _) =>
        {
            Activate();
            UpdateShades(new Rect(0, 0, ActualWidth, ActualHeight));
        };
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = true;
        _start = e.GetPosition(this);
        SelectionRect.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateRect(_start, _start);
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        var (px, py) = DipToPixels(pos.X, pos.Y);
        CursorLabel.Text = $"{px}, {py}";
        Canvas.SetLeft(CursorBadge, Math.Min(pos.X + 16, ActualWidth - 90));
        Canvas.SetTop(CursorBadge, Math.Min(pos.Y + 16, ActualHeight - 36));

        if (!_dragging) return;
        UpdateRect(_start, pos);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || e.ChangedButton != MouseButton.Left) return;
        _dragging = false;
        ReleaseMouseCapture();

        var end = e.GetPosition(this);
        var rect = Normalize(_start, end);
        if (rect.Width < 3 || rect.Height < 3)
        {
            SelectionRect.Visibility = Visibility.Collapsed;
            UpdateShades(new Rect(0, 0, ActualWidth, ActualHeight));
            return;
        }

        Selection = DipRectToPixelSelection(rect);
        Confirmed = true;
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Confirmed = false;
            Close();
        }
    }

    private void UpdateRect(Point a, Point b)
    {
        var r = Normalize(a, b);
        Canvas.SetLeft(SelectionRect, r.X);
        Canvas.SetTop(SelectionRect, r.Y);
        SelectionRect.Width = r.Width;
        SelectionRect.Height = r.Height;

        var sel = DipRectToPixelSelection(r);
        SizeLabel.Text = $"{sel.Width} × {sel.Height}";
        Canvas.SetLeft(SizeBadge, r.X);
        Canvas.SetTop(SizeBadge, Math.Max(0, r.Y - 28));
        SizeBadge.Visibility = r.Width > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateShades(r);
    }

    private void UpdateShades(Rect r)
    {
        double w = ActualWidth, h = ActualHeight;
        Place(ShadeTop, 0, 0, w, Math.Max(0, r.Y));
        Place(ShadeBottom, 0, r.Y + r.Height, w, Math.Max(0, h - (r.Y + r.Height)));
        Place(ShadeLeft, 0, r.Y, Math.Max(0, r.X), r.Height);
        Place(ShadeRight, r.X + r.Width, r.Y, Math.Max(0, w - (r.X + r.Width)), r.Height);
    }

    private (int X, int Y) DipToPixels(double x, double y)
    {
        double scaleX = _frame.Width / Math.Max(ActualWidth, 1);
        double scaleY = _frame.Height / Math.Max(ActualHeight, 1);
        return (
            (int)Math.Round(x * scaleX),
            (int)Math.Round(y * scaleY));
    }

    private Int32Rect DipRectToPixelSelection(Rect rect)
    {
        double scaleX = _frame.Width / Math.Max(ActualWidth, 1);
        double scaleY = _frame.Height / Math.Max(ActualHeight, 1);
        int x = (int)Math.Round(rect.X * scaleX);
        int y = (int)Math.Round(rect.Y * scaleY);
        int w = Math.Max(1, (int)Math.Round(rect.Width * scaleX));
        int h = Math.Max(1, (int)Math.Round(rect.Height * scaleY));

        // Clamp to native frame pixels so DPI rounding never invents out-of-range crops.
        x = Math.Clamp(x, 0, _frame.Width - 1);
        y = Math.Clamp(y, 0, _frame.Height - 1);
        w = Math.Clamp(w, 1, _frame.Width - x);
        h = Math.Clamp(h, 1, _frame.Height - y);
        return new Int32Rect(x, y, w, h);
    }

    private static void Place(Rectangle rect, double x, double y, double w, double h)
    {
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        rect.Width = Math.Max(0, w);
        rect.Height = Math.Max(0, h);
    }

    private static Rect Normalize(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        double w = Math.Abs(a.X - b.X);
        double h = Math.Abs(a.Y - b.Y);
        return new Rect(x, y, w, h);
    }
}
