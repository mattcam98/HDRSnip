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

        Left = frame.MonitorBounds.Left;
        Top = frame.MonitorBounds.Top;
        Width = frame.MonitorBounds.Width;
        Height = frame.MonitorBounds.Height;

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
        CursorLabel.Text = $"{(int)pos.X}, {(int)pos.Y}";
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

        var scaleX = _frame.Width / Math.Max(ActualWidth, 1);
        var scaleY = _frame.Height / Math.Max(ActualHeight, 1);
        Selection = new Int32Rect(
            (int)Math.Round(rect.X * scaleX),
            (int)Math.Round(rect.Y * scaleY),
            Math.Max(1, (int)Math.Round(rect.Width * scaleX)),
            Math.Max(1, (int)Math.Round(rect.Height * scaleY)));

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
        SizeLabel.Text = $"{(int)r.Width} × {(int)r.Height}";
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
