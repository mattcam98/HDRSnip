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

    public override Annotation WithSize(double size) => this with { Thickness = size };

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        var pen = MakePen(Color, Thickness, PenLineCap.Flat, PenLineJoin.Miter);
        if (IsEllipse)
            dc.DrawEllipse(null, pen, new Point(Rect.X + Rect.Width / 2, Rect.Y + Rect.Height / 2), Rect.Width / 2, Rect.Height / 2);
        else
            dc.DrawRectangle(null, pen, Rect);
    }

    public override bool HitTest(Point point, double tolerance)
    {
        Geometry geometry = IsEllipse
            ? new EllipseGeometry(Rect)
            : new RectangleGeometry(Rect);
        return Freeze(geometry).StrokeContains(HitPen(Thickness, tolerance), point);
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

    public override Rect Bounds
    {
        get
        {
            var formatted = Format();
            return new Rect(Origin, new Size(
                Math.Max(formatted.WidthIncludingTrailingWhitespace, FontSize / 2),
                Math.Max(formatted.Height, FontSize)));
        }
    }

    public override Annotation Translate(Vector delta) => this with { Origin = Origin + delta };

    public override Annotation WithSize(double size) => this with { FontSize = size };

    public override void Draw(DrawingContext dc, BitmapSource source) => dc.DrawText(Format(), Origin);

    public override bool HitTest(Point point, double tolerance)
    {
        var rect = Bounds;
        rect.Inflate(tolerance, tolerance);
        return rect.Contains(point);
    }

    private FormattedText Format() => new(
        Text,
        CultureInfo.CurrentUICulture,
        FlowDirection.LeftToRight,
        Typeface,
        FontSize,
        Freeze(new SolidColorBrush(Color)),
        pixelsPerDip: 1.0);
}

/// <summary>
/// Redacts a region by averaging it into blocks. The pixelated tile is computed
/// from the source once per rectangle and reused for every redraw.
/// </summary>
public sealed record PixelateAnnotation : Annotation
{
    public required Int32Rect Rect { get; init; }
    public int BlockSize { get; init; } = 12;

    private BitmapSource? _tile;
    private (BitmapSource Source, Int32Rect Rect, int Block) _tileKey;

    public override Rect Bounds => new(Rect.X, Rect.Y, Rect.Width, Rect.Height);

    // The copied cache misses on its key, so a moved mark re-samples on first draw.
    public override Annotation Translate(Vector delta) => this with
    {
        Rect = new Int32Rect(
            (int)Math.Round(Rect.X + delta.X), (int)Math.Round(Rect.Y + delta.Y), Rect.Width, Rect.Height)
    };

    public override Annotation WithColor(Color color) => this;

    public override void Draw(DrawingContext dc, BitmapSource source)
    {
        var clipped = Clip(Rect, source.PixelWidth, source.PixelHeight);
        if (clipped.Width <= 0 || clipped.Height <= 0)
            return;

        if (_tile is null || _tileKey != (source, clipped, BlockSize))
        {
            _tile = BuildTile(source, clipped, BlockSize);
            _tileKey = (source, clipped, BlockSize);
        }

        dc.DrawImage(_tile, new Rect(clipped.X, clipped.Y, clipped.Width, clipped.Height));
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

    private static BitmapSource BuildTile(BitmapSource source, Int32Rect rect, int block)
    {
        if (source.Format != PixelFormats.Bgra32)
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = rect.Width, height = rect.Height, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(rect, pixels, stride, 0);

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

        var tile = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        tile.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        tile.Freeze();
        return tile;
    }
}
