using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HDRSnip.Editing;

/// <summary>
/// The editing surface. Draws the capture and its marks in image pixel space
/// (one DIP here is one source pixel; the editor scales the whole element for
/// zoom), turns mouse gestures into annotations, and hosts the inline text
/// editor as its only child. All state changes go through <see cref="History"/>.
/// </summary>
public sealed class MarkupCanvas : Canvas
{
    // Screen-space constants, divided by ViewScale so they feel the same at any zoom.
    private const double HitSlackPx = 6;
    private const double HandleSizePx = 9;
    private const double DragThresholdPx = 3;
    private const double MinShapePx = 3;

    private static readonly Color CropScrim = Color.FromArgb(0x8C, 0x06, 0x08, 0x10);

    public event Action? SelectionChanged;
    public event Action? TextEditingChanged;
    public event Action? ToolChanged;

    private BitmapSource? _source;
    private MarkupTool _tool = MarkupTool.Select;
    private double _viewScale = 1;

    private readonly Dictionary<MarkupTool, (Color Color, double Size)> _styles = new()
    {
        [MarkupTool.Pen] = (Palette.Red, 4),
        [MarkupTool.Highlighter] = (Palette.Yellow, 18),
        [MarkupTool.Line] = (Palette.Red, 4),
        [MarkupTool.Arrow] = (Palette.Red, 4),
        [MarkupTool.Rectangle] = (Palette.Red, 4),
        [MarkupTool.Ellipse] = (Palette.Red, 4),
        [MarkupTool.Text] = (Palette.Red, 28),
    };

    // In-flight gesture
    private bool _dragging;
    private Point _origin;
    private Annotation? _draft;
    private List<Point>? _strokePoints;
    private Annotation? _dragSource;   // the mark being moved by the select tool
    private bool _moved;

    // Selection
    private Annotation? _selected;

    // Crop
    private Rect _cropDraft;
    private CropHandle _cropHandle;
    private Rect _cropAnchor;

    // Text
    private readonly TextBox _textEditor;
    private TextAnnotation? _editingText;
    private Point _textOrigin;

    public MarkupCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;   // the image is the focus visual; no dotted frame
        ClipToBounds = true;
        Background = Brushes.Transparent;
        History = new MarkupHistory(MarkupDocument.Empty(1, 1));
        History.Changed += OnHistoryChanged;

        _textEditor = BuildTextEditor();
        Children.Add(_textEditor);
        UpdateCursor();
    }

    // ------------------------------------------------------------ state

    public MarkupHistory History { get; }

    public MarkupDocument Document => History.Current;

    public bool IsEditingText => _textEditor.Visibility == Visibility.Visible;

    public Annotation? Selected => _selected;

    public MarkupTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
                return;

            CommitTextEdit();
            CancelGesture();
            if (value != MarkupTool.Select)
                SetSelected(null);

            _tool = value;
            if (_tool == MarkupTool.Crop)
                _cropDraft = ToRect(Document.Crop);

            UpdateLayoutSize();
            UpdateCursor();
            InvalidateVisual();
            ToolChanged?.Invoke();
        }
    }

    /// <summary>Editor zoom, used to keep handles and hit slack a constant screen size.</summary>
    public double ViewScale
    {
        get => _viewScale;
        set
        {
            _viewScale = Math.Max(value, 0.01);
            InvalidateVisual();
        }
    }

    /// <summary>The tool whose colour and size the style controls are editing.</summary>
    public MarkupTool StyleTarget => _tool == MarkupTool.Select && _selected is not null
        ? ToolFor(_selected)
        : _tool;

    public bool HasStyle => _styles.ContainsKey(StyleTarget);

    public Color Color
    {
        get => _styles.TryGetValue(StyleTarget, out var style) ? style.Color : Palette.Red;
        set
        {
            var target = StyleTarget;
            if (!_styles.ContainsKey(target))
                return;

            _styles[target] = (value, _styles[target].Size);
            if (_selected is not null)
                ReplaceSelected(_selected.WithColor(value));
            if (IsEditingText)
                _textEditor.Foreground = new SolidColorBrush(value);
        }
    }

    public double Size
    {
        get => _styles.TryGetValue(StyleTarget, out var style) ? style.Size : 4;
        set
        {
            var target = StyleTarget;
            if (!_styles.ContainsKey(target))
                return;

            var (min, max) = SizeRange;
            value = Math.Clamp(Math.Round(value), min, max);
            _styles[target] = (_styles[target].Color, value);
            if (_selected is not null)
                ReplaceSelected(_selected.WithSize(value));
            if (IsEditingText)
                _textEditor.FontSize = value;
        }
    }

    public (double Min, double Max) SizeRange => StyleTarget switch
    {
        MarkupTool.Text => (12, 96),
        MarkupTool.Highlighter => (6, 48),
        _ => (1, 32)
    };

    public void Load(BitmapSource source)
    {
        CommitTextEdit();
        CancelGesture();
        _source = source;
        SetSelected(null);
        History.Reset(MarkupDocument.Empty(source.PixelWidth, source.PixelHeight));
    }

    public BitmapSource Flatten() => _source is null
        ? throw new InvalidOperationException("No image loaded.")
        : Document.Flatten(_source);

    // ------------------------------------------------------------ commands

    public void Undo()
    {
        CommitTextEdit();
        CancelGesture();
        SetSelected(null);
        History.Undo();
    }

    public void Redo()
    {
        CommitTextEdit();
        CancelGesture();
        SetSelected(null);
        History.Redo();
    }

    public void DeleteSelected()
    {
        if (_selected is null)
            return;

        var doomed = _selected;
        SetSelected(null);
        History.Commit(Document.Remove(doomed));
    }

    public void ClearMarks()
    {
        CommitTextEdit();
        CancelGesture();
        SetSelected(null);
        History.Commit(Document with { Annotations = Document.Annotations.Clear() });
    }

    /// <summary>Escape: unwinds the innermost thing in progress. Returns false if there was nothing.</summary>
    public bool Cancel()
    {
        if (IsEditingText)
        {
            CancelTextEdit();
            return true;
        }

        if (_dragging)
        {
            CancelGesture();
            return true;
        }

        if (_tool == MarkupTool.Crop)
        {
            Tool = MarkupTool.Select;
            return true;
        }

        if (_selected is not null)
        {
            SetSelected(null);
            return true;
        }

        if (_tool != MarkupTool.Select)
        {
            Tool = MarkupTool.Select;
            return true;
        }

        return false;
    }

    public void ApplyCrop()
    {
        if (_tool != MarkupTool.Crop || _source is null)
            return;

        var crop = ToInt32Rect(_cropDraft, _source.PixelWidth, _source.PixelHeight);
        if (crop.Width >= 1 && crop.Height >= 1)
            History.Commit(Document with { Crop = crop });

        Tool = MarkupTool.Select;
    }

    /// <summary>Finishes any text being typed so an export includes it.</summary>
    public void CommitPendingEdits() => CommitTextEdit();

    /// <summary>Arrow keys: moves the selected mark by whole pixels.</summary>
    public void NudgeSelected(Vector delta)
    {
        if (_selected is not null)
            ReplaceSelected(_selected.Translate(delta));
    }

    // ------------------------------------------------------------ text editing

    private TextBox BuildTextEditor()
    {
        var box = new TextBox
        {
            Visibility = Visibility.Collapsed,
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            MinWidth = 8,
            FontFamily = TextAnnotation.Typeface.FontFamily,
            FontWeight = TextAnnotation.Typeface.Weight,
            FocusVisualStyle = null,
        };

        // A bare template so the caret sits exactly where FormattedText will draw.
        var template = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(FocusableProperty, false);
        template.VisualTree = host;
        box.Template = template;

        box.LostKeyboardFocus += (_, _) => CommitTextEdit();
        box.PreviewKeyDown += OnTextEditorKeyDown;
        box.TextChanged += (_, _) => InvalidateVisual();
        return box;
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelTextEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            CommitTextEdit();
            e.Handled = true;
        }
    }

    private void BeginTextEdit(TextAnnotation? existing, Point origin)
    {
        CommitTextEdit();
        SetSelected(null);

        _editingText = existing;
        _textOrigin = existing?.Origin ?? origin;
        var color = existing?.Color ?? Color;
        double fontSize = existing?.FontSize ?? Size;

        _textEditor.Text = existing?.Text ?? string.Empty;
        _textEditor.FontSize = fontSize;
        _textEditor.Foreground = new SolidColorBrush(color);
        _textEditor.CaretBrush = _textEditor.Foreground;
        _textEditor.SelectionBrush = new SolidColorBrush(Color.FromArgb(0x55, color.R, color.G, color.B));

        var offset = ContentOffset;
        SetLeft(_textEditor, _textOrigin.X - offset.X);
        SetTop(_textEditor, _textOrigin.Y - offset.Y);
        _textEditor.Visibility = Visibility.Visible;
        _textEditor.CaretIndex = _textEditor.Text.Length;

        Dispatcher.BeginInvoke(() => _textEditor.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        InvalidateVisual();
        TextEditingChanged?.Invoke();
    }

    private void CommitTextEdit()
    {
        if (!IsEditingText)
            return;

        // Hide first: the focus change below re-enters here and must see "not editing".
        var existing = EndTextEdit();
        string text = _textEditor.Text.TrimEnd('\r', '\n', ' ');

        if (string.IsNullOrWhiteSpace(text))
        {
            if (existing is not null)
                History.Commit(Document.Remove(existing));
            return;
        }

        var replacement = new TextAnnotation
        {
            Origin = _textOrigin,
            Text = text,
            FontSize = _textEditor.FontSize,
            Color = ((SolidColorBrush)_textEditor.Foreground).Color
        };
        History.Commit(existing is null ? Document.Add(replacement) : Document.Replace(existing, replacement));
    }

    private void CancelTextEdit()
    {
        if (IsEditingText)
            EndTextEdit();
    }

    /// <summary>Hides the editor and returns the mark it was editing, if any.</summary>
    private TextAnnotation? EndTextEdit()
    {
        var existing = _editingText;
        _editingText = null;
        _textEditor.Visibility = Visibility.Collapsed;
        if (Keyboard.FocusedElement == _textEditor)
            Focus();
        InvalidateVisual();
        TextEditingChanged?.Invoke();
        return existing;
    }

    // ------------------------------------------------------------ input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (_source is null || (IsEditingText && _textEditor.IsMouseOver))
            return;

        Focus();   // commits any text edit via LostKeyboardFocus
        var point = ToImage(e.GetPosition(this));
        _origin = point;
        _moved = false;

        switch (_tool)
        {
            case MarkupTool.Select:
            {
                var hit = HitAnnotation(point);
                if (e.ClickCount == 2 && hit is TextAnnotation text)
                {
                    BeginTextEdit(text, point);
                    return;
                }

                SetSelected(hit);
                _dragSource = hit;
                break;
            }

            case MarkupTool.Text:
            {
                var hit = HitAnnotation(point) as TextAnnotation;
                BeginTextEdit(hit, point);
                return;
            }

            case MarkupTool.Pen:
            case MarkupTool.Highlighter:
                _strokePoints = [point];
                _draft = BuildStroke();
                break;

            case MarkupTool.Crop:
                _cropHandle = HitCropHandle(point);
                _cropAnchor = _cropDraft;
                if (_cropHandle == CropHandle.None)
                {
                    _cropHandle = CropHandle.New;
                    _cropDraft = new Rect(point, point);
                }
                break;
        }

        _dragging = true;
        CaptureMouse();
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_source is null)
            return;

        var point = ToImage(e.GetPosition(this));

        if (!_dragging)
        {
            UpdateHoverCursor(point);
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var delta = point - _origin;

        switch (_tool)
        {
            case MarkupTool.Select:
                if (_dragSource is null)
                    return;
                if (!_moved && delta.Length * _viewScale < DragThresholdPx)
                    return;
                _moved = true;
                _draft = _dragSource.Translate(delta);
                break;

            case MarkupTool.Pen:
            case MarkupTool.Highlighter:
                if (_strokePoints is null)
                    return;
                if (shift && _strokePoints.Count >= 1)
                {
                    // Shift turns the pen into a ruler: origin to the cursor, nothing in between.
                    if (_strokePoints.Count > 1)
                        _strokePoints.RemoveRange(1, _strokePoints.Count - 1);
                    _strokePoints.Add(point);
                }
                else if ((point - _strokePoints[^1]).Length * _viewScale >= 1.0)
                {
                    _strokePoints.Add(point);
                }
                _draft = BuildStroke();
                break;

            case MarkupTool.Line:
            case MarkupTool.Arrow:
                _draft = new LineAnnotation
                {
                    From = _origin,
                    To = shift ? SnapAngle(_origin, point) : point,
                    Thickness = Size,
                    Color = Color,
                    HasArrow = _tool == MarkupTool.Arrow
                };
                break;

            case MarkupTool.Rectangle:
            case MarkupTool.Ellipse:
                _draft = new ShapeAnnotation
                {
                    Rect = Normalize(_origin, point, square: shift),
                    Thickness = Size,
                    Color = Color,
                    IsEllipse = _tool == MarkupTool.Ellipse
                };
                break;

            case MarkupTool.Pixelate:
            {
                var rect = ToInt32Rect(Normalize(_origin, point, square: shift), _source.PixelWidth, _source.PixelHeight);
                _draft = rect.Width > 0 && rect.Height > 0
                    ? new PixelateAnnotation { Rect = rect, BlockSize = PixelateBlock(_source) }
                    : null;
                break;
            }

            case MarkupTool.Crop:
                _cropDraft = DragCrop(_cropAnchor, _cropHandle, _origin, point);
                break;
        }

        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging)
            return;

        _dragging = false;
        ReleaseMouseCapture();

        switch (_tool)
        {
            case MarkupTool.Select:
                if (_moved && _dragSource is not null && _draft is not null)
                {
                    History.Commit(Document.Replace(_dragSource, _draft));
                    SetSelected(_draft);
                }
                break;

            case MarkupTool.Pen:
            case MarkupTool.Highlighter:
                if (_draft is not null)
                    History.Commit(Document.Add(_draft));
                break;

            case MarkupTool.Line:
            case MarkupTool.Arrow:
            case MarkupTool.Rectangle:
            case MarkupTool.Ellipse:
            case MarkupTool.Pixelate:
                if (_draft is not null && IsBigEnough(_draft))
                    History.Commit(Document.Add(_draft));
                break;

            case MarkupTool.Crop:
                if (_cropDraft.Width < 1 || _cropDraft.Height < 1)
                    _cropDraft = _cropAnchor;
                break;
        }

        _draft = null;
        _dragSource = null;
        _strokePoints = null;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_dragging)
            CancelGesture();
        else if (_selected is not null)
            SetSelected(null);
        else if (_tool != MarkupTool.Select && _tool != MarkupTool.Crop)
            Tool = MarkupTool.Select;
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragging)
            CancelGesture();
    }

    private void CancelGesture()
    {
        if (_dragging)
        {
            _dragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();
        }

        if (_tool == MarkupTool.Crop && _cropHandle != CropHandle.None)
            _cropDraft = _cropAnchor;

        _cropHandle = CropHandle.None;
        _draft = null;
        _dragSource = null;
        _strokePoints = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------ rendering

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_source is null)
            return;

        var offset = ContentOffset;
        dc.PushTransform(new TranslateTransform(-offset.X, -offset.Y));

        dc.DrawImage(_source, new Rect(0, 0, _source.PixelWidth, _source.PixelHeight));

        var hidden = _editingText ?? (_moved ? _dragSource : null);
        foreach (var annotation in Document.Annotations)
        {
            if (!ReferenceEquals(annotation, hidden))
                annotation.Draw(dc, _source);
        }

        _draft?.Draw(dc, _source);

        if (_tool == MarkupTool.Crop)
            DrawCropChrome(dc);
        else if (_selected is not null && !_dragging)
            DrawSelection(dc, _selected.Bounds);
        else if (_moved && _draft is not null)
            DrawSelection(dc, _draft.Bounds);

        if (IsEditingText)
            DrawTextFrame(dc);

        dc.Pop();
    }

    private void DrawSelection(DrawingContext dc, Rect bounds)
    {
        double px = 1 / _viewScale;
        bounds.Inflate(4 * px, 4 * px);

        // A dark halo under the dashed line keeps it visible on any capture.
        var halo = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0)), 3 * px);
        halo.Freeze();
        dc.DrawRectangle(null, halo, bounds);
        dc.DrawRectangle(null, DashedPen(px), bounds);
    }

    private void DrawTextFrame(DrawingContext dc)
    {
        double px = 1 / _viewScale;
        var size = _textEditor.RenderSize;
        var rect = new Rect(_textOrigin, new Size(Math.Max(size.Width, 8), Math.Max(size.Height, _textEditor.FontSize)));
        rect.Inflate(3 * px, 2 * px);
        dc.DrawRectangle(null, DashedPen(px), rect);
    }

    private Pen DashedPen(double px)
    {
        var pen = new Pen(AccentBrush, px) { DashStyle = new DashStyle([4, 3], 0) };
        pen.Freeze();
        return pen;
    }

    private void DrawCropChrome(DrawingContext dc)
    {
        if (_source is null)
            return;

        double px = 1 / _viewScale;
        var full = new Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        var crop = Rect.Intersect(_cropDraft, full);
        if (crop.IsEmpty)
            crop = new Rect(0, 0, 0, 0);

        var scrim = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(crop));
        scrim.Freeze();
        dc.DrawGeometry(new SolidColorBrush(CropScrim), null, scrim);

        // Rule-of-thirds guides help line a crop up without a grid setting.
        var guide = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), px);
        guide.Freeze();
        for (int i = 1; i < 3; i++)
        {
            double x = crop.X + crop.Width * i / 3, y = crop.Y + crop.Height * i / 3;
            dc.DrawLine(guide, new Point(x, crop.Top), new Point(x, crop.Bottom));
            dc.DrawLine(guide, new Point(crop.Left, y), new Point(crop.Right, y));
        }

        var frame = new Pen(AccentBrush, 1.5 * px);
        frame.Freeze();
        dc.DrawRectangle(null, frame, crop);

        double handle = HandleSizePx * px;
        var fill = Brushes.White;
        foreach (var (_, at) in CropHandles(crop))
            dc.DrawRoundedRectangle(fill, frame, new Rect(at.X - handle / 2, at.Y - handle / 2, handle, handle), px, px);
    }

    /// <summary>The app accent for the current theme, so chrome matches the rest of the window.</summary>
    private Brush AccentBrush => TryFindResource("Accent.Default") as Brush ?? FallbackAccent;

    private static readonly SolidColorBrush FallbackAccent = new(Color.FromRgb(0x4C, 0xC2, 0xFF));

    static MarkupCanvas() => FallbackAccent.Freeze();

    // ------------------------------------------------------------ crop geometry

    private enum CropHandle { None, New, Move, N, S, E, W, NE, NW, SE, SW }

    private static IEnumerable<(CropHandle Handle, Point At)> CropHandles(Rect r)
    {
        yield return (CropHandle.NW, r.TopLeft);
        yield return (CropHandle.N, new Point(r.X + r.Width / 2, r.Top));
        yield return (CropHandle.NE, r.TopRight);
        yield return (CropHandle.E, new Point(r.Right, r.Y + r.Height / 2));
        yield return (CropHandle.SE, r.BottomRight);
        yield return (CropHandle.S, new Point(r.X + r.Width / 2, r.Bottom));
        yield return (CropHandle.SW, r.BottomLeft);
        yield return (CropHandle.W, new Point(r.Left, r.Y + r.Height / 2));
    }

    private CropHandle HitCropHandle(Point p)
    {
        double slack = (HandleSizePx / 2 + HitSlackPx) / _viewScale;
        foreach (var (handle, at) in CropHandles(_cropDraft))
        {
            if (Math.Abs(p.X - at.X) <= slack && Math.Abs(p.Y - at.Y) <= slack)
                return handle;
        }

        return _cropDraft.Contains(p) ? CropHandle.Move : CropHandle.None;
    }

    private Rect DragCrop(Rect anchor, CropHandle handle, Point from, Point to)
    {
        if (_source is null)
            return anchor;

        var full = new Rect(0, 0, _source.PixelWidth, _source.PixelHeight);
        var delta = to - from;

        if (handle == CropHandle.New)
            return Rect.Intersect(Normalize(from, to, square: false), full);

        if (handle == CropHandle.Move)
        {
            double x = Math.Clamp(anchor.X + delta.X, 0, full.Width - anchor.Width);
            double y = Math.Clamp(anchor.Y + delta.Y, 0, full.Height - anchor.Height);
            return new Rect(x, y, anchor.Width, anchor.Height);
        }

        double left = anchor.Left, top = anchor.Top, right = anchor.Right, bottom = anchor.Bottom;
        if (handle is CropHandle.W or CropHandle.NW or CropHandle.SW) left = Math.Clamp(anchor.Left + delta.X, 0, full.Width);
        if (handle is CropHandle.E or CropHandle.NE or CropHandle.SE) right = Math.Clamp(anchor.Right + delta.X, 0, full.Width);
        if (handle is CropHandle.N or CropHandle.NW or CropHandle.NE) top = Math.Clamp(anchor.Top + delta.Y, 0, full.Height);
        if (handle is CropHandle.S or CropHandle.SW or CropHandle.SE) bottom = Math.Clamp(anchor.Bottom + delta.Y, 0, full.Height);

        return new Rect(new Point(Math.Min(left, right), Math.Min(top, bottom)), new Point(Math.Max(left, right), Math.Max(top, bottom)));
    }

    private static Cursor CursorFor(CropHandle handle) => handle switch
    {
        CropHandle.N or CropHandle.S => Cursors.SizeNS,
        CropHandle.E or CropHandle.W => Cursors.SizeWE,
        CropHandle.NE or CropHandle.SW => Cursors.SizeNESW,
        CropHandle.NW or CropHandle.SE => Cursors.SizeNWSE,
        CropHandle.Move => Cursors.SizeAll,
        _ => Cursors.Cross
    };

    // ------------------------------------------------------------ helpers

    private Vector ContentOffset => _tool == MarkupTool.Crop
        ? new Vector(0, 0)
        : new Vector(Document.Crop.X, Document.Crop.Y);

    private Point ToImage(Point local) => local + ContentOffset;

    private void UpdateLayoutSize()
    {
        if (_source is null)
            return;

        bool full = _tool == MarkupTool.Crop;
        Width = full ? _source.PixelWidth : Document.Crop.Width;
        Height = full ? _source.PixelHeight : Document.Crop.Height;

        if (IsEditingText)
        {
            var offset = ContentOffset;
            SetLeft(_textEditor, _textOrigin.X - offset.X);
            SetTop(_textEditor, _textOrigin.Y - offset.Y);
        }
    }

    private void OnHistoryChanged()
    {
        if (_selected is not null && !Document.Annotations.Contains(_selected))
            SetSelected(null);

        if (_tool == MarkupTool.Crop && !_dragging)
            _cropDraft = ToRect(Document.Crop);

        UpdateLayoutSize();
        InvalidateVisual();
    }

    private void SetSelected(Annotation? annotation)
    {
        if (ReferenceEquals(_selected, annotation))
            return;

        _selected = annotation;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private void ReplaceSelected(Annotation replacement)
    {
        if (_selected is null || ReferenceEquals(_selected, replacement))
            return;

        var old = _selected;
        History.Commit(Document.Replace(old, replacement));
        _selected = replacement;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private Annotation? HitAnnotation(Point point)
    {
        double slack = HitSlackPx / _viewScale;
        var marks = Document.Annotations;
        for (int i = marks.Length - 1; i >= 0; i--)
        {
            if (marks[i].HitTest(point, slack))
                return marks[i];
        }
        return null;
    }

    private StrokeAnnotation BuildStroke() => new()
    {
        Points = _strokePoints!.ToArray(),
        Thickness = Size,
        Color = Color,
        IsHighlighter = _tool == MarkupTool.Highlighter
    };

    /// <summary>Blocks scale with the capture so UI text is unreadable at 1080p and 4K alike.</summary>
    private static int PixelateBlock(BitmapSource source) =>
        Math.Max(10, (int)Math.Round(Math.Min(source.PixelWidth, source.PixelHeight) / 64.0));

    /// <summary>Rejects the accidental click-without-drag that would leave an invisible shape.</summary>
    private bool IsBigEnough(Annotation annotation)
    {
        double min = MinShapePx / _viewScale;
        var extent = annotation switch
        {
            LineAnnotation line => new Rect(line.From, line.To),
            ShapeAnnotation shape => shape.Rect,
            _ => annotation.Bounds
        };
        return extent.Width >= min || extent.Height >= min;
    }

    private void UpdateCursor()
    {
        Cursor = _tool switch
        {
            MarkupTool.Select => Cursors.Arrow,
            MarkupTool.Text => Cursors.IBeam,
            _ => Cursors.Cross
        };
    }

    private void UpdateHoverCursor(Point point)
    {
        Cursor = _tool switch
        {
            MarkupTool.Select => HitAnnotation(point) is not null ? Cursors.SizeAll : Cursors.Arrow,
            MarkupTool.Crop => CursorFor(HitCropHandle(point)),
            MarkupTool.Text => Cursors.IBeam,
            _ => Cursors.Cross
        };
    }

    private static MarkupTool ToolFor(Annotation annotation) => annotation switch
    {
        StrokeAnnotation { IsHighlighter: true } => MarkupTool.Highlighter,
        StrokeAnnotation => MarkupTool.Pen,
        LineAnnotation { HasArrow: true } => MarkupTool.Arrow,
        LineAnnotation => MarkupTool.Line,
        ShapeAnnotation { IsEllipse: true } => MarkupTool.Ellipse,
        ShapeAnnotation => MarkupTool.Rectangle,
        TextAnnotation => MarkupTool.Text,
        _ => MarkupTool.Pixelate
    };

    private static Rect Normalize(Point a, Point b, bool square)
    {
        double w = b.X - a.X, h = b.Y - a.Y;
        if (square)
        {
            double side = Math.Max(Math.Abs(w), Math.Abs(h));
            w = Math.Sign(w) * side;
            h = Math.Sign(h) * side;
        }
        return new Rect(new Point(Math.Min(a.X, a.X + w), Math.Min(a.Y, a.Y + h)), new Size(Math.Abs(w), Math.Abs(h)));
    }

    /// <summary>Snaps a line to the nearest 45° so Shift gives clean horizontals and diagonals.</summary>
    private static Point SnapAngle(Point from, Point to)
    {
        var v = to - from;
        if (v.Length < 0.5)
            return to;

        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        return from + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
    }

    private static Rect ToRect(Int32Rect r) => new(r.X, r.Y, r.Width, r.Height);

    private static Int32Rect ToInt32Rect(Rect r, int width, int height)
    {
        int x = Math.Clamp((int)Math.Round(r.X), 0, width);
        int y = Math.Clamp((int)Math.Round(r.Y), 0, height);
        int right = Math.Clamp((int)Math.Round(r.Right), 0, width);
        int bottom = Math.Clamp((int)Math.Round(r.Bottom), 0, height);
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
