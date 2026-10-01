using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HDRSnip.Editing;

/// <summary>
/// One mark on top of a capture. Annotations are immutable and live in image
/// pixel space, so the same object draws the on-screen preview at any zoom and
/// the final composite at 1:1. Editing produces a new record via <c>with</c>,
/// which is what makes undo a matter of keeping the previous document around.
/// </summary>
public abstract record Annotation
{
    public Color Color { get; init; } = Colors.Red;

    /// <summary>Extent in image pixels, including stroke width.</summary>
    public abstract Rect Bounds { get; }

    public abstract Annotation Translate(Vector delta);

    public abstract void Draw(DrawingContext dc, BitmapSource source);

    /// <summary>Whether <paramref name="point"/> lies on the mark, with a screen-derived slack.</summary>
    public abstract bool HitTest(Point point, double tolerance);

    /// <summary>Applies a new stroke width or font size; marks without one return themselves.</summary>
    public virtual Annotation WithSize(double size) => this;

    public virtual Annotation WithColor(Color color) => this with { Color = color };

    /// <summary>Fills a shape or backs text with its colour; marks with nothing to fill return themselves.</summary>
    public virtual Annotation WithFill(bool fill) => this;

    /// <summary>Points the select tool can drag to reshape the mark. Empty when it can only be moved.</summary>
    public virtual IReadOnlyList<Point> Handles => [];

    public virtual Annotation MoveHandle(int index, Point to) => this;

    /// <summary>Corners in clockwise order from top-left, so the one opposite index i is (i + 2) % 4.</summary>
    protected static Point[] Corners(Rect rect) => [rect.TopLeft, rect.TopRight, rect.BottomRight, rect.BottomLeft];

    /// <summary>The rectangle after dragging one corner, pinned at the opposite corner.</summary>
    protected static Rect DragCorner(Rect rect, int index, Point to) => new(Corners(rect)[(index + 2) % 4], to);

    protected static Pen MakePen(Color color, double thickness, PenLineCap cap = PenLineCap.Round, PenLineJoin join = PenLineJoin.Round)
    {
        var pen = new Pen(Freeze(new SolidColorBrush(color)), thickness)
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = join
        };
        pen.Freeze();
        return pen;
    }

    protected static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>A pen that is only used for hit-testing, so colour is irrelevant.</summary>
    protected static Pen HitPen(double thickness, double tolerance) =>
        MakePen(Colors.Black, thickness + tolerance * 2);
}

/// <summary>Freehand pen or highlighter stroke.</summary>
public sealed record StrokeAnnotation : Annotation
{
    private const double HighlighterOpacity = 0.42;

    public required IReadOnlyList<Point> Points { get; init; }
    public double Thickness { get; init; } = 4;
    public bool IsHighlighter { get; init; }

    public override Rect Bounds
    {
        get
        {
            var rect = Rect.Empty;
            foreach (var point in Points)
                rect.Union(point);
            rect.Inflate(Thickness / 2, Thickness / 2);
            return rect;
        }
    }

    public override Annotation Translate(Vector delta) =>
        this with { Points = Points.Select(p => p + delta).ToArray() };

    public override Annotation WithSize(double size) => this with { Thickness = size };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        if (IsHighlighter)
            dc.PushOpacity(HighlighterOpacity);

        if (Points.Count == 1)
        {
            // A click without a drag still leaves a mark, as a real pen would.
            dc.DrawEllipse(Freeze(new SolidColorBrush(Color)), null, Points[0], Thickness / 2, Thickness / 2);
        }
        else
        {
            // One geometry, one stroke: overlapping segments of a translucent
            // highlighter never double up because the fill is resolved once.
            dc.DrawGeometry(null, MakePen(Color, Thickness, IsHighlighter ? PenLineCap.Square : PenLineCap.Round), BuildGeometry());
        }

        if (IsHighlighter)
            dc.Pop();
    }

    public override bool HitTest(Point point, double tolerance)
    {
        if (Points.Count == 1)
            return (point - Points[0]).Length <= Thickness / 2 + tolerance;

        return BuildGeometry().StrokeContains(HitPen(Thickness, tolerance), point);
    }

    private StreamGeometry BuildGeometry()
    {
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(Points[0], false, false);
            context.PolyLineTo(Points.Skip(1).ToList(), true, true);
        }
        return Freeze(geometry);
    }
}

/// <summary>Straight line, optionally with an arrow head at the end.</summary>
public sealed record LineAnnotation : Annotation
{
    public required Point From { get; init; }
    public required Point To { get; init; }
    public double Thickness { get; init; } = 4;
    public bool HasArrow { get; init; }

    public override Rect Bounds
    {
        get
        {
            var rect = new Rect(From, To);
            double pad = HasArrow ? HeadLength : Thickness / 2;
            rect.Inflate(pad, pad);
            return rect;
        }
    }

    private double HeadLength => Math.Max(Thickness * 3.2, 10);

    public override Annotation Translate(Vector delta) => this with { From = From + delta, To = To + delta };

    public override IReadOnlyList<Point> Handles => [From, To];

    public override Annotation MoveHandle(int index, Point to) => index == 0 ? this with { From = to } : this with { To = to };

    public override Annotation WithSize(double size) => this with { Thickness = size };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        var pen = MakePen(Color, Thickness);
        if (!HasArrow || (To - From).Length < 0.5)
        {
            dc.DrawLine(pen, From, To);
            return;
        }

        var direction = To - From;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        double head = HeadLength;

        // Shorten the shaft so its round cap does not poke through the tip.
        var shaftEnd = To - direction * (head * 0.75);
        dc.DrawLine(pen, From, shaftEnd);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(To, true, true);
            context.LineTo(To - direction * head + normal * (head * 0.5), true, true);
            context.LineTo(To - direction * head - normal * (head * 0.5), true, true);
        }
        dc.DrawGeometry(Freeze(new SolidColorBrush(Color)), MakePen(Color, Math.Min(Thickness, 2), PenLineCap.Round, PenLineJoin.Round), Freeze(geometry));
    }

    public override bool HitTest(Point point, double tolerance) =>
        Freeze(new LineGeometry(From, To)).StrokeContains(HitPen(HasArrow ? Math.Max(Thickness, HeadLength * 0.6) : Thickness, tolerance), point);
}

/// <summary>Outlined rectangle or ellipse.</summary>
public sealed record ShapeAnnotation : Annotation
{
    public required Rect Rect { get; init; }
    public double Thickness { get; init; } = 4;
    public bool IsEllipse { get; init; }
    public bool IsFilled { get; init; }

    public override Rect Bounds
    {
        get
        {
            var rect = Rect;
            rect.Inflate(Thickness / 2, Thickness / 2);
            return rect;
        }
    }

    public override Annotation Translate(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };

    public override Annotation WithFill(bool fill) => this with { IsFilled = fill };

    public override IReadOnlyList<Point> Handles => Corners(Rect);

    public override Annotation MoveHandle(int index, Point to) => this with { Rect = DragCorner(Rect, index, to) };

    public override Annotation WithSize(double size) => this with { Thickness = size };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        var pen = MakePen(Color, Thickness, PenLineCap.Flat, PenLineJoin.Miter);
        var fill = IsFilled ? Freeze(new SolidColorBrush(Color)) : null;
        if (IsEllipse)
            dc.DrawEllipse(fill, pen, new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2), Rect.Width / 2, Rect.Height / 2);
        else
            dc.DrawRectangle(fill, pen, Rect);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        Geometry geometry = IsEllipse
            ? new EllipseGeometry(Rect)
            : new RectangleGeometry(Rect);
        geometry.Freeze();
        return geometry.StrokeContains(HitPen(Thickness, tolerance), point) || (IsFilled && geometry.FillContains(point));
    }
}

/// <summary>A block of text anchored at its top-left corner.</summary>
public sealed record TextAnnotation : Annotation
{
    public static readonly Typeface Typeface = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public required Point Origin { get; init; }
    public required string Text { get; init; }
    public double FontSize { get; init; } = 24;

    /// <summary>Draws the text on a plate of its colour, for legibility over busy captures.</summary>
    public bool HasBackground { get; init; }

    private double Padding => HasBackground ? FontSize * 0.3 : 0;

    public override Rect Bounds
    {
        get
        {
            var formatted = Format(Color);
            var rect = new Rect(Origin, new Size(
                Math.Max(formatted.WidthIncludingTrailingWhitespace, FontSize / 2),
                Math.Max(formatted.Height, FontSize)));
            rect.Inflate(Padding, Padding * 0.5);
            return rect;
        }
    }

    public override Annotation Translate(Vector delta) => this with { Origin = Origin + delta };

    public override Annotation WithSize(double size) => this with { FontSize = size };

    public override Annotation WithFill(bool fill) => this with { HasBackground = fill };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        if (HasBackground)
        {
            double radius = FontSize * 0.2;
            dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color)), null, Bounds, radius, radius);
        }

        dc.DrawText(Format(HasBackground ? InkOn(Color) : Color), Origin);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var rect = Bounds;
        rect.Inflate(tolerance, tolerance);
        return rect.Contains(point);
    }

    /// <summary>Black on light colours, white on dark ones, by perceived brightness.</summary>
    public static Color InkOn(Color background) =>
        0.299 * background.R + 0.587 * background.G + 0.114 * background.B > 160 ? Colors.Black : Colors.White;

    private FormattedText Format(Color ink) => new(
        Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        Typeface,
        FontSize,
        Freeze(new SolidColorBrush(ink)),
        pixelsPerDip: 1.0);
}

/// <summary>A numbered step marker: a filled disc with its number, for walking through a sequence.</summary>
public sealed record NumberAnnotation : Annotation
{
    public required Point Center { get; init; }
    public required int Number { get; init; }
    public double FontSize { get; init; } = 28;

    private double Radius => FontSize * 0.8;

    public override Rect Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override Annotation Translate(Vector delta) => this with { Center = Center + delta };

    public override Annotation WithSize(double size) => this with { FontSize = size };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        dc.DrawEllipse(Freeze(new SolidColorBrush(Color)), null, Center, Radius, Radius);

        var label = new FormattedText(
            Number.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            TextAnnotation.Typeface,
            FontSize,
            Freeze(new SolidColorBrush(TextAnnotation.InkOn(Color))),
            pixelsPerDip: 1.0);
        dc.DrawText(label, new Point(Center.X - label.Width / 2, Center.Y - label.Height / 2));
    }

    public override bool HitTest(Point point, double tolerance) => (point - Center).Length <= Radius + tolerance;
}

public enum RedactMode
{
    Pixelate,
    Blur,
    Solid
}

/// <summary>
/// Hides a region: averaged into blocks, blurred, or covered outright. Solid is
/// the only mode that destroys the content beyond recovery. The pixelated or
/// blurred tile is computed from the source once per rectangle and reused for
/// every redraw.
/// </summary>
public sealed record PixelateAnnotation : Annotation
{
    public required Int32Rect Rect { get; init; }
    public int BlockSize { get; init; } = 12;
    public RedactMode Mode { get; init; }

    private BitmapSource? _tile;
    private (BitmapSource Source, Int32Rect Rect, int Block, RedactMode Mode) _tileKey;

    public override Rect Bounds => new(Rect.X, Rect.Y, Rect.Width, Rect.Height);

    // The copied cache misses on its key, so a moved mark re-samples on first draw.
    public override Annotation Translate(Vector delta) => this with
    {
        Rect = new Int32Rect(
            (int)Math.Round(Rect.X + delta.X), (int)Math.Round(Rect.Y + delta.Y), Rect.Width, Rect.Height)
    };

    public override IReadOnlyList<Point> Handles => Corners(Bounds);

    public override Annotation MoveHandle(int index, Point to)
    {
        var rect = DragCorner(Bounds, index, to);
        return this with
        {
            Rect = new Int32Rect(
                (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
                Math.Max(1, (int)Math.Round(rect.Width)), Math.Max(1, (int)Math.Round(rect.Height)))
        };
    }

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        var clipped = Clip(Rect, source.PixelWidth, source.PixelHeight);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return;

        var area = new Rect(clipped.X, clipped.Y, clipped.Width, clipped.Height);
        if (Mode == RedactMode.Solid)
        {
            dc.DrawRectangle(Freeze(new SolidColorBrush(Color)), null, area);
            return;
        }

        if (_tile is null || _tileKey != (source, clipped, BlockSize, Mode))
        {
            _tile = BuildTile(source, clipped, BlockSize, Mode);
            _tileKey = (source, clipped, BlockSize, Mode);
        }

        dc.DrawImage(_tile, area);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        var rect = Bounds;
        rect.Inflate(tolerance, tolerance);
        return rect.Contains(point);
    }

    private static Int32Rect Clip(Int32Rect rect, int width, int height)
    {
        int x = Math.Clamp(rect.X, 0, width);
        int y = Math.Clamp(rect.Y, 0, height);
        int right = Math.Clamp(rect.X + rect.Width, 0, width);
        int bottom = Math.Clamp(rect.Y + rect.Height, 0, height);
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static WriteableBitmap BuildTile(BitmapSource source, Int32Rect rect, int block, RedactMode mode)
    {
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = rect.Width, height = rect.Height, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(rect, pixels, stride, 0);

        if (mode == RedactMode.Blur)
            Blur(pixels, width, height, block);
        else
            Pixelate(pixels, width, height, block);

        var tile = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        tile.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        tile.Freeze();
        return tile;
    }

    /// <summary>Three box-blur passes each way, which is visually a Gaussian of about the same radius.</summary>
    private static void Blur(byte[] pixels, int width, int height, int radius)
    {
        var scratch = new byte[pixels.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(pixels, scratch, width, height, radius, horizontal: true);
            BoxBlur(scratch, pixels, width, height, radius, horizontal: false);
        }
    }

    private static void BoxBlur(byte[] from, byte[] to, int width, int height, int radius, bool horizontal)
    {
        int length = horizontal ? width : height, lines = horizontal ? height : width;
        int step = horizontal ? 4 : width * 4;
        int window = radius * 2 + 1;

        for (int line = 0; line < lines; line++)
        {
            int start = horizontal ? line * width * 4 : line * 4;
            for (int channel = 0; channel < 4; channel++)
            {
                // Running sum over a window that clamps at both ends of the line.
                int sum = 0;
                for (int i = -radius; i <= radius; i++)
                    sum += from[start + Math.Clamp(i, 0, length - 1) * step + channel];

                for (int i = 0; i < length; i++)
                {
                    to[start + i * step + channel] = (byte)(sum / window);
                    sum += from[start + Math.Min(i + radius + 1, length - 1) * step + channel]
                         - from[start + Math.Max(i - radius, 0) * step + channel];
                }
            }
        }
    }

    private static void Pixelate(byte[] pixels, int width, int height, int block)
    {
        int stride = width * 4;

        for (int by = 0; by < height; by += block)
        {
            int bh = Math.Min(block, height - by);
            for (int bx = 0; bx < width; bx += block)
            {
                int bw = Math.Min(block, width - bx);
                long b = 0, g = 0, r = 0, a = 0;

                for (int y = by; y < by + bh; y++)
                {
                    int row = y * stride + bx * 4;
                    for (int x = 0; x < bw; x++, row += 4)
                    {
                        b += pixels[row];
                        g += pixels[row + 1];
                        r += pixels[row + 2];
                        a += pixels[row + 3];
                    }
                }

                int count = bw * bh;
                byte ab = (byte)(b / count), ag = (byte)(g / count), ar = (byte)(r / count), aa = (byte)(a / count);

                for (int y = by; y < by + bh; y++)
                {
                    int row = y * stride + bx * 4;
                    for (int x = 0; x < bw; x++, row += 4)
                    {
                        pixels[row] = ab;
                        pixels[row + 1] = ag;
                        pixels[row + 2] = ar;
                        pixels[row + 3] = aa;
                    }
                }
            }
        }
    }
}

/// <summary>
/// Keeps a region at full brightness while the rest of the capture is dimmed.
/// The dimming itself is drawn once by the document, so several spotlights
/// share one scrim instead of darkening each other.
/// </summary>
public sealed record SpotlightAnnotation : Annotation
{
    public required Rect Rect { get; init; }

    public override Rect Bounds => Rect;

    public override Annotation Translate(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };

    public override Annotation WithColor(Color color) => this;

    public override IReadOnlyList<Point> Handles => Corners(Rect);

    public override Annotation MoveHandle(int index, Point to) => this with { Rect = DragCorner(Rect, index, to) };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
    }

    // Only the edge: the interior must stay clickable for the marks inside it.
    public override bool HitTest(Point point, double tolerance) =>
        Freeze(new RectangleGeometry(Rect)).StrokeContains(HitPen(0, tolerance), point);
}
