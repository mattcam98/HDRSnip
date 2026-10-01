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
    /// Flattens the document into a plain bitmap for the clipboard or disk.
    /// Returns the source itself when there is nothing to flatten, so an
    /// unmarked capture round-trips byte-for-byte.
    /// </summary>
    public BitmapSource Flatten(BitmapSource source)
    {
        if (IsPristine(source))
            return source;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-Crop.X, -Crop.Y));
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));
            foreach (var annotation in Annotations)
                annotation.Draw(dc, source);
            dc.Pop();
        }

        // Rendered at 96 DPI so one DIP is one pixel, then re-tagged with the
        // source DPI: a marked-up capture must paste at the same size as a plain one.
        var target = new RenderTargetBitmap(Crop.Width, Crop.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);

        // Straight alpha keeps every consumer happy: DIB, PNG and any future encoder.
        int stride = Crop.Width * 4;
        var pixels = new byte[stride * Crop.Height];
        new FormatConvertedBitmap(target, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, stride, 0);

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
