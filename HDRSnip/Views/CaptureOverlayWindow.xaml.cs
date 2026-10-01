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
/// Dragging selects a region; a plain click takes the window under the cursor.
/// With adjustment on, a dragged region stays on screen with handles until
/// Enter or a double-click confirms it.
/// </summary>
public partial class CaptureOverlayWindow : Window
{
    private const double MinimumDragDip = 3;
    private const double CornerTickLength = 14;
    private const double BadgeGap = 10;
    private const double HandleSize = 9;
    private const double HandleSlack = 8;

    /// <summary>Frame pixels shown across the loupe; odd, so one pixel sits dead centre.</summary>
    private const int LoupePixels = 15;
    private const double LoupeOffset = 22;

    private enum Mode { Idle, Dragging, Adjusting, AdjustDragging }

    private enum Handle { None, Move, N, S, E, W, NE, NW, SE, SW }

    private readonly CapturedFrame _frame;
    private readonly bool _adjust;
    private readonly ImageBrush _loupeBrush;

    /// <summary>Window bounds in frame pixels, front to back, as they were when the frame was grabbed.</summary>
    private readonly List<Int32Rect> _windows = [];

    private Mode _mode;
    private Point _origin;
    private Rect _region;        // the selection being adjusted, in DIPs
    private Rect _anchor;        // _region when the current handle drag began
    private Handle _handle;
    private bool _hintDismissed;

    /// <summary>The chosen region in frame pixels, or null if cancelled.</summary>
    public Int32Rect? Selection { get; private set; }

    public CaptureOverlayWindow(
        CapturedFrame frame, BitmapSource preview, IEnumerable<System.Drawing.Rectangle> windows, bool adjust)
    {
        _frame = frame;
        _adjust = adjust;
        InitializeComponent();

        foreach (var window in windows)
        {
            var visible = System.Drawing.Rectangle.Intersect(window, frame.MonitorBounds);
            if (!visible.IsEmpty)
            {
                _windows.Add(new Int32Rect(
                    visible.X - frame.MonitorBounds.Left, visible.Y - frame.MonitorBounds.Top, visible.Width, visible.Height));
            }
        }

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
        HdrNoteDivider.Visibility = HdrNote.Visibility;

        _loupeBrush = new ImageBrush(preview) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        LoupeImage.Fill = _loupeBrush;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        ShadeOuter.Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        ShadeHole.Rect = Rect.Empty;

        Canvas.SetLeft(Hint, (ActualWidth - Hint.ActualWidth) / 2);
        Canvas.SetTop(Hint, Math.Max(24, ActualHeight * 0.06));

        var position = Mouse.GetPosition(this);
        HighlightWindowAt(position);
        UpdateLoupe(position);
    }

    // ------------------------------------------------------------ interaction

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        DismissHint();
        _origin = e.GetPosition(this);

        if (_mode == Mode.Adjusting)
        {
            _handle = HitHandle(_origin);
            if (_handle == Handle.Move && e.ClickCount == 2)
            {
                Commit(_region);
                return;
            }

            if (_handle != Handle.None)
            {
                _anchor = _region;
                _mode = Mode.AdjustDragging;
                CaptureMouse();
                return;
            }
        }

        // Anywhere else starts a fresh selection.
        _mode = Mode.Dragging;
        Handles.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Cross;
        CaptureMouse();
        ShowSelection(new Rect(_origin, _origin), drawn: true);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        UpdateLoupe(position);

        switch (_mode)
        {
            case Mode.Dragging:
                ShowSelection(Normalize(_origin, position), drawn: true);
                break;

            case Mode.AdjustDragging:
                SetRegion(DragRegion(_anchor, _handle, _origin, position));
                break;

            case Mode.Adjusting:
                Cursor = CursorFor(HitHandle(position));
                break;

            default:
                HighlightWindowAt(position);
                break;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (_mode == Mode.AdjustDragging)
        {
            _mode = Mode.Adjusting;
            ReleaseMouseCapture();
            return;
        }

        if (_mode != Mode.Dragging)
            return;

        _mode = Mode.Idle;
        ReleaseMouseCapture();

        var region = Normalize(_origin, e.GetPosition(this));
        if (region.Width < MinimumDragDip || region.Height < MinimumDragDip)
        {
            // A click without a drag takes the window under it; with no window
            // there, start again rather than capturing one pixel.
            if (WindowAt(_origin) is { } window)
            {
                Selection = window;
                Close();
            }
            else
            {
                ShowSelection(Rect.Empty);
            }
        }
        else if (_adjust)
        {
            _mode = Mode.Adjusting;
            HintText.Text = "Enter or double-click to capture";
            Hint.BeginAnimation(OpacityProperty, null);
            Hint.Opacity = 1;
            SetRegion(region);
        }
        else
        {
            Commit(region);
        }
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_mode == Mode.AdjustDragging)
            _mode = Mode.Adjusting;
        else if (_mode == Mode.Dragging)
            _mode = Mode.Idle;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            return;
        }

        if (_mode != Mode.Adjusting)
            return;

        // One frame pixel per press, ten with Ctrl; Shift moves the far edge instead of the whole region.
        double step = (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 10 : 1) * ActualWidth / _frame.Width;
        Vector delta = e.Key switch
        {
            Key.Left => new Vector(-step, 0),
            Key.Right => new Vector(step, 0),
            Key.Up => new Vector(0, -step),
            Key.Down => new Vector(0, step),
            _ => default
        };

        if (e.Key == Key.Enter)
        {
            Commit(_region);
        }
        else if (delta != default)
        {
            var handle = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Handle.SE : Handle.Move;
            SetRegion(DragRegion(_region, handle, default, (Point)delta));
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void OnCancel(object sender, MouseButtonEventArgs e) => Cancel();

    private void Cancel()
    {
        Selection = null;
        Close();
    }

    private void Commit(Rect region)
    {
        Selection = ToFramePixels(region);
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

    private void HighlightWindowAt(Point position) =>
        ShowSelection(WindowAt(position) is { } window ? ToDips(window) : Rect.Empty);

    private void SetRegion(Rect region)
    {
        _region = region;
        ShowSelection(region, drawn: true);
        Handles.Data = BuildHandles(region);
        Handles.Visibility = Visibility.Visible;
    }

    /// <summary>Outlines a region and punches it out of the dim. An empty region clears both.</summary>
    private void ShowSelection(Rect region, bool drawn = false)
    {
        bool visible = drawn || !region.IsEmpty;
        SelectionBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Corners.Visibility = SelectionBox.Visibility;
        if (!visible)
        {
            SizeBadge.Visibility = Visibility.Collapsed;
            ShadeHole.Rect = Rect.Empty;
            return;
        }

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

    /// <summary>Magnified pixels around the cursor with its frame coordinates, for pixel-exact edges.</summary>
    private void UpdateLoupe(Point position)
    {
        if (ActualWidth < 1 || ActualHeight < 1)
            return;

        int x = Math.Clamp((int)(position.X * _frame.Width / ActualWidth), 0, _frame.Width - 1);
        int y = Math.Clamp((int)(position.Y * _frame.Height / ActualHeight), 0, _frame.Height - 1);

        // The brush viewbox is in the bitmap's own DIPs, which differ from pixels on a scaled monitor.
        var source = (BitmapSource)_loupeBrush.ImageSource;
        double dipsPerPixelX = 96.0 / source.DpiX, dipsPerPixelY = 96.0 / source.DpiY;
        int half = LoupePixels / 2;
        _loupeBrush.Viewbox = new Rect(
            (x - half) * dipsPerPixelX, (y - half) * dipsPerPixelY, LoupePixels * dipsPerPixelX, LoupePixels * dipsPerPixelY);
        LoupeLabel.Text = $"{x}, {y}";

        Loupe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = Loupe.DesiredSize.Width, height = Loupe.DesiredSize.Height;
        double left = position.X + LoupeOffset, top = position.Y + LoupeOffset;
        if (left + width > ActualWidth) left = position.X - LoupeOffset - width;
        if (top + height > ActualHeight) top = position.Y - LoupeOffset - height;

        Canvas.SetLeft(Loupe, Math.Max(0, left));
        Canvas.SetTop(Loupe, Math.Max(0, top));
        Loupe.Visibility = Visibility.Visible;
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

    private static Geometry BuildHandles(Rect region)
    {
        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var (_, at) in HandlePoints(region))
        {
            group.Children.Add(new RectangleGeometry(
                new Rect(at.X - HandleSize / 2, at.Y - HandleSize / 2, HandleSize, HandleSize), 1.5, 1.5));
        }

        group.Freeze();
        return group;
    }

    // ------------------------------------------------------------ adjustment

    private static IEnumerable<(Handle Handle, Point At)> HandlePoints(Rect r)
    {
        yield return (Handle.NW, r.TopLeft);
        yield return (Handle.N, new Point(r.X + r.Width / 2, r.Top));
        yield return (Handle.NE, r.TopRight);
        yield return (Handle.E, new Point(r.Right, r.Y + r.Height / 2));
        yield return (Handle.SE, r.BottomRight);
        yield return (Handle.S, new Point(r.X + r.Width / 2, r.Bottom));
        yield return (Handle.SW, r.BottomLeft);
        yield return (Handle.W, new Point(r.Left, r.Y + r.Height / 2));
    }

    private Handle HitHandle(Point p)
    {
        foreach (var (handle, at) in HandlePoints(_region))
        {
            if (Math.Abs(p.X - at.X) <= HandleSlack && Math.Abs(p.Y - at.Y) <= HandleSlack)
                return handle;
        }

        return _region.Contains(p) ? Handle.Move : Handle.None;
    }

    /// <summary>Moves or resizes a region by a drag, keeping it on screen and never inside out.</summary>
    private Rect DragRegion(Rect anchor, Handle handle, Point from, Point to)
    {
        var delta = to - from;
        if (handle == Handle.Move)
        {
            return new Rect(
                Math.Clamp(anchor.X + delta.X, 0, Math.Max(0, ActualWidth - anchor.Width)),
                Math.Clamp(anchor.Y + delta.Y, 0, Math.Max(0, ActualHeight - anchor.Height)),
                anchor.Width, anchor.Height);
        }

        double left = anchor.Left, top = anchor.Top, right = anchor.Right, bottom = anchor.Bottom;
        if (handle is Handle.W or Handle.NW or Handle.SW) left = Math.Clamp(left + delta.X, 0, ActualWidth);
        if (handle is Handle.E or Handle.NE or Handle.SE) right = Math.Clamp(right + delta.X, 0, ActualWidth);
        if (handle is Handle.N or Handle.NW or Handle.NE) top = Math.Clamp(top + delta.Y, 0, ActualHeight);
        if (handle is Handle.S or Handle.SW or Handle.SE) bottom = Math.Clamp(bottom + delta.Y, 0, ActualHeight);

        return new Rect(
            new Point(Math.Min(left, right), Math.Min(top, bottom)),
            new Point(Math.Max(left, right), Math.Max(top, bottom)));
    }

    private static Cursor CursorFor(Handle handle) => handle switch
    {
        Handle.N or Handle.S => Cursors.SizeNS,
        Handle.E or Handle.W => Cursors.SizeWE,
        Handle.NE or Handle.SW => Cursors.SizeNESW,
        Handle.NW or Handle.SE => Cursors.SizeNWSE,
        Handle.Move => Cursors.SizeAll,
        _ => Cursors.Cross
    };

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

    /// <summary>The frontmost window under a point, in frame pixels.</summary>
    private Int32Rect? WindowAt(Point position)
    {
        int x = (int)(position.X * _frame.Width / Math.Max(ActualWidth, 1));
        int y = (int)(position.Y * _frame.Height / Math.Max(ActualHeight, 1));

        foreach (var window in _windows)
        {
            if (x >= window.X && x < window.X + window.Width && y >= window.Y && y < window.Y + window.Height)
                return window;
        }

        return null;
    }

    private Rect ToDips(Int32Rect pixels)
    {
        double scaleX = Math.Max(ActualWidth, 1) / _frame.Width;
        double scaleY = Math.Max(ActualHeight, 1) / _frame.Height;
        return new Rect(pixels.X * scaleX, pixels.Y * scaleY, pixels.Width * scaleX, pixels.Height * scaleY);
    }

    private static Rect Normalize(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}
