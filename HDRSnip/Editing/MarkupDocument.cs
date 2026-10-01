using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HDRSnip.Editing;

public enum MarkupTool
{
    Select,
    Pen,
    Highlighter,
    Line,
    Arrow,
    Rectangle,
    Ellipse,
    Text,
    Number,
    Spotlight,
    Eyedropper,
    Pixelate,
    Crop
}

/// <summary>
/// Everything the user has done to a capture: a stack of marks plus a crop
/// rectangle, all in source pixel coordinates. The crop is a view over the
/// original rather than a destructive cut, so it can be widened again later
/// and undone like anything else.
/// </summary>
public sealed record MarkupDocument(ImmutableArray<Annotation> Annotations, Int32Rect Crop)
{
    public static MarkupDocument Empty(int width, int height) =>
        new(ImmutableArray<Annotation>.Empty, new Int32Rect(0, 0, width, height));

    public bool IsCropped(BitmapSource source) =>
        Crop != new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight);

    public bool IsPristine(BitmapSource source) => Annotations.IsEmpty && !IsCropped(source);

    public MarkupDocument Add(Annotation annotation) => this with { Annotations = Annotations.Add(annotation) };

    public MarkupDocument Remove(Annotation annotation) => this with { Annotations = Annotations.Remove(annotation) };

    public MarkupDocument Replace(Annotation old, Annotation replacement) =>
        this with { Annotations = Annotations.Replace(old, replacement) };

    /// <summary>
    /// Draws every mark in order, over one shared scrim for the spotlights.
    /// </summary>
    /// <param name="hidden">A mark to leave out, because it is being edited or dragged.</param>
    /// <param name="draft">A mark still being drawn, not yet part of the document.</param>
    public void DrawMarks(DrawingContext dc, BitmapSource source, Annotation? hidden = null, Annotation? draft = null)
    {
        var marks = Annotations.Where(mark => !ReferenceEquals(mark, hidden)).ToList();
        if (draft is not null)
            marks.Add(draft);

        Geometry? scrim = null;
        foreach (var spotlight in marks.OfType<SpotlightAnnotation>())
        {
            scrim = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                scrim ?? new RectangleGeometry(new Rect(0, 0, source.PixelWidth, source.PixelHeight)),
                new RectangleGeometry(spotlight.Rect));
        }

        if (scrim is not null)
            dc.DrawGeometry(SpotlightScrim, null, scrim);

        foreach (var mark in marks)
            mark.Draw(dc, source);
    }

    private static readonly SolidColorBrush SpotlightScrim = CreateScrim();

    private static SolidColorBrush CreateScrim()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0));
        brush.Freeze();
        return brush;
    }

    /// <summary>The marks alone on a transparent, crop-sized bitmap, or null when there are none.</summary>
    public BitmapSource? RenderMarks(BitmapSource source)
    {
        if (Annotations.IsEmpty)
            return null;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-Crop.X, -Crop.Y));
            DrawMarks(dc, source);
            dc.Pop();
        }

        var marks = new RenderTargetBitmap(Crop.Width, Crop.Height, 96, 96, PixelFormats.Pbgra32);
        marks.Render(visual);
        marks.Freeze();
        return marks;
    }

    /// <summary>
    /// Flattens the document into a plain bitmap for the clipboard or disk.
    /// Returns the source itself when there is nothing to flatten, so an
    /// unmarked capture round-trips byte-for-byte.
    /// </summary>
    public BitmapSource Flatten(BitmapSource source)
    {
        if (IsPristine(source))
            return source;

        BitmapSource straight = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = Crop.Width * 4;
        var pixels = new byte[stride * Crop.Height];
        straight.CopyPixels(Crop, pixels, stride, 0);

        // The marks are rendered on their own and blended in here, rather than
        // drawing the capture through WPF as well: its renderer shifts mid-tones
        // by a level, and a pixel no mark touches must come out exactly as captured.
        if (RenderMarks(source) is { } marks)
        {
            var overlay = new byte[pixels.Length];
            marks.CopyPixels(overlay, stride, 0);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = overlay[i + 3];
                if (alpha == 0)
                    continue;

                // "Over" in premultiplied space, then back to straight alpha.
                int under = pixels[i + 3], keep = 255 - alpha;
                int outAlpha = alpha + under * keep / 255;
                for (int c = 0; c < 3; c++)
                {
                    int premultiplied = overlay[i + c] + pixels[i + c] * under / 255 * keep / 255;
                    pixels[i + c] = (byte)Math.Min(255, premultiplied * 255 / outAlpha);
                }

                pixels[i + 3] = (byte)outAlpha;
            }
        }

        var flat = BitmapSource.Create(
            Crop.Width, Crop.Height, source.DpiX, source.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        flat.Freeze();
        return flat;
    }
}

/// <summary>
/// Linear undo history over immutable documents. Because every edit yields a
/// whole new document, undo and redo are just pointer swaps.
/// </summary>
public sealed class MarkupHistory
{
    private readonly Stack<MarkupDocument> _undo = new();
    private readonly Stack<MarkupDocument> _redo = new();

    public MarkupHistory(MarkupDocument initial) => Current = initial;

    public MarkupDocument Current { get; private set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after Current changes for any reason.</summary>
    public event Action? Changed;

    public void Commit(MarkupDocument next)
    {
        if (ReferenceEquals(next, Current) || next == Current)
            return;

        _undo.Push(Current);
        _redo.Clear();
        Current = next;
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (!CanUndo)
            return false;

        _redo.Push(Current);
        Current = _undo.Pop();
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!CanRedo)
            return false;

        _undo.Push(Current);
        Current = _redo.Pop();
        Changed?.Invoke();
        return true;
    }

    public void Reset(MarkupDocument document)
    {
        _undo.Clear();
        _redo.Clear();
        Current = document;
        Changed?.Invoke();
    }
}
