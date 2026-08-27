using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Interop;
using Point = System.Windows.Point;

namespace HDRSnip.Views;

/// <summary>
/// Full-screen selection overlay drawn over a frozen capture of the monitor.
/// Freezing first is what makes the selection honest: what you drag over is
/// exactly what gets cropped, even if the desktop keeps animating underneath.
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private const double MinimumDragDip = 3;
    private const double CornerTickLength = 14;
    private const double BadgeGap = 10;

    private readonly CapturedFrame _frame;
    private Point _origin;
    private bool _dragging;
    private bool _hintDismissed;

    /// <summary>The chosen region in frame pixels, or null if cancelled.</summary>
    public Int32Rect? Selection { get; private set; }

    public CaptureOverlayWindow(CapturedFrame frame, BitmapSource preview)
    {
        _frame = frame;
        InitializeComponent();

        // DXGI bounds are physical pixels; WPF positions windows in DIPs.
        double scale = Native.GetMonitorScale(
            frame.MonitorBounds.Left + frame.MonitorBounds.Width / 2,
            frame.MonitorBounds.Top + frame.MonitorBounds.Height / 2);
        if (scale < 0.01) scale = 1.0;

        Left = frame.MonitorBounds.Left / scale;
        Top = frame.MonitorBounds.Top / scale;
        Width = frame.MonitorBounds.Width / scale;
        Height = frame.MonitorBounds.Height / scale;

        Frozen.Source = preview;
        HdrNote.Visibility = frame.WasHdr ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        ShadeOuter.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        ShadeHole.Rect = Rect.Empty;

        Canvas.SetLeft(Hint, (ActualWidth - Hint.ActualWidth) / 2);
        Canvas.SetTop(Hint, Math.Max(24, ActualHeight * 0.06));
    }

    // ------------------------------------------------------------ interaction

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        DismissHint();
        _dragging = true;
        _origin = e.GetPosition(this);
        Selection = null;
        SelectionBox.Visibility = Visibility.Visible;
        Corners.Visibility = Visibility.Visible;
        CaptureMouse();
        UpdateSelection(_origin, _origin);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        UpdateSelection(_origin, e.GetPosition(this));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || e.ChangedButton != MouseButton.Left)
            return;

        _dragging = false;
        ReleaseMouseCapture();

        var region = Normalize(_origin, e.GetPosition(this));
        if (region.Width < MinimumDragDip || region.Height < MinimumDragDip)
        {
            // Treat a stray click as "start again" rather than capturing one pixel.
            SelectionBox.Visibility = Visibility.Collapsed;
            Corners.Visibility = Visibility.Collapsed;
            SizeBadge.Visibility = Visibility.Collapsed;
            ShadeHole.Rect = Rect.Empty;
            return;
        }

        Selection = ToFramePixels(region);
        Close();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Cancel();
    }

    private void OnCancel(object sender, MouseButtonEventArgs e) => Cancel();

    private void Cancel()
    {
        Selection = null;
        Close();
    }

    private void DismissHint()
    {
        if (_hintDismissed)
            return;

        _hintDismissed = true;
        Hint.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(120),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    // ------------------------------------------------------------ chrome

    private void UpdateSelection(Point a, Point b)
    {
        var region = Normalize(a, b);

        Canvas.SetLeft(SelectionBox, region.X);
        Canvas.SetTop(SelectionBox, region.Y);
        SelectionBox.Width = region.Width;
        SelectionBox.Height = region.Height;

        ShadeHole.Rect = region;
        Corners.Data = BuildCornerTicks(region);

        var pixels = ToFramePixels(region);
        SizeLabel.Text = $"{pixels.Width} × {pixels.Height}";
        SizeBadge.Visibility = region.Width > 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaceBadge(region);
    }

    /// <summary>Keeps the dimension badge outside the selection and inside the screen.</summary>
    private void PlaceBadge(Rect region)
    {
        SizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = SizeBadge.DesiredSize.Width;
        double height = SizeBadge.DesiredSize.Height;

        double top = region.Y - height - BadgeGap;
        if (top < 0)
            top = Math.Min(region.Bottom + BadgeGap, ActualHeight - height);

        double left = Math.Clamp(region.X, 0, Math.Max(0, ActualWidth - width));

        Canvas.SetLeft(SizeBadge, left);
        Canvas.SetTop(SizeBadge, Math.Max(0, top));
    }

    private static Geometry BuildCornerTicks(Rect r)
    {
        double tick = Math.Min(CornerTickLength, Math.Min(r.Width, r.Height) / 2);
        if (tick <= 0)
            return Geometry.Empty;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            Corner(context, new Point(r.Left, r.Top), tick, tick);
            Corner(context, new Point(r.Right, r.Top), -tick, tick);
            Corner(context, new Point(r.Right, r.Bottom), -tick, -tick);
            Corner(context, new Point(r.Left, r.Bottom), tick, -tick);
        }

        geometry.Freeze();
        return geometry;

        static void Corner(StreamGeometryContext context, Point at, double dx, double dy)
        {
            context.BeginFigure(new Point(at.X + dx, at.Y), false, false);
            context.LineTo(at, true, false);
            context.LineTo(new Point(at.X, at.Y + dy), true, false);
        }
    }

    // ------------------------------------------------------------ geometry

    /// <summary>
    /// Converts a DIP rectangle to frame pixels. The frame may not match the
    /// window one-to-one when DPI scaling is non-integral, so the result is
    /// clamped rather than trusted.
    /// </summary>
    private Int32Rect ToFramePixels(Rect region)
    {
        double scaleX = _frame.Width / Math.Max(ActualWidth, 1);
        double scaleY = _frame.Height / Math.Max(ActualHeight, 1);

        int x = Math.Clamp((int)Math.Round(region.X * scaleX), 0, Math.Max(0, _frame.Width - 1));
        int y = Math.Clamp((int)Math.Round(region.Y * scaleY), 0, Math.Max(0, _frame.Height - 1));
        int width = Math.Clamp((int)Math.Round(region.Width * scaleX), 1, _frame.Width - x);
        int height = Math.Clamp((int)Math.Round(region.Height * scaleY), 1, _frame.Height - y);

        return new Int32Rect(x, y, width, height);
    }

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
