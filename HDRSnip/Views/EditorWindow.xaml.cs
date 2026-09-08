using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Editing;
using HDRSnip.Services;
using Microsoft.Win32;
using DataObject = System.Windows.DataObject;

namespace HDRSnip.Views;

/// <summary>
/// Post-capture review and markup: check the result, draw on it, crop it, then
/// copy or save. It stays open and reloads in place when a new capture arrives,
/// unless it holds unsaved markup, in which case the tray opens a second window.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly double[] ZoomStops = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4];

    private readonly CaptureService _capture;
    private readonly Dictionary<MarkupTool, RadioButton> _toolButtons = new();
    private readonly List<RadioButton> _swatches = new();
    private CaptureResult _result = null!;
    private string? _savedPath;
    private double? _zoom;          // null means fit to viewport
    private bool _dirty;            // edits since the last copy or save
    private bool _syncingStyle;
    private BitmapSource? _flattened;

    public EditorWindow(CaptureResult result, CaptureService capture)
    {
        InitializeComponent();
        _capture = capture;

        BuildSwatches();
        foreach (var button in FindToolButtons(this))
            _toolButtons[Enum.Parse<MarkupTool>((string)button.Tag)] = button;

        Markup.History.Changed += OnHistoryChanged;
        Markup.SelectionChanged += SyncStyle;
        Markup.ToolChanged += OnCanvasToolChanged;
        Markup.TextEditingChanged += UpdateHint;

        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);
        Load(result);
    }

    /// <summary>True when the image on screen differs from anything copied or saved.</summary>
    public bool HasUnsavedEdits => _dirty && _result is not null && !Markup.Document.IsPristine(_result.Image);

    /// <summary>Swaps in a newer capture without recreating the window.</summary>
    public void Load(CaptureResult result)
    {
        _result = result;
        _savedPath = result.SavedPath;
        _flattened = null;

        Markup.Load(result.Image);
        Markup.Tool = MarkupTool.Select;
        _dirty = false;   // after Load: resetting the history counts as a change
        HdrBadge.Visibility = result.WasHdr ? Visibility.Visible : Visibility.Collapsed;

        _zoom = null;
        ApplyZoom();
        UpdateChrome();
        SyncStyle();

        Status.Text = string.IsNullOrEmpty(_savedPath)
            ? "Copied to clipboard"
            : $"Saved to {_savedPath}";
    }

    public void ShowStatus(string text) => Status.Text = text;

    /// <summary>The image as the user sees it: source plus marks, trimmed to the crop.</summary>
    private BitmapSource Flattened => _flattened ??= Markup.Flatten();

    // ------------------------------------------------------------ export

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Markup.CommitPendingEdits();
        var image = Flattened;

        var data = new DataObject();
        data.SetImage(image);

        try
        {
            var png = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(png);
            png.Position = 0;
            data.SetData("PNG", png, false);
        }
        catch (Exception ex)
        {
            // The DIB alone still pastes everywhere that matters.
            App.LogError("EditorCopyPng", ex);
        }

        try
        {
            Clipboard.SetDataObject(data, copy: true);
            _dirty = false;
            Status.Text = "Copied to clipboard";
        }
        catch (Exception ex)
        {
            App.LogError("EditorCopy", ex);
            Status.Text = "Another app is holding the clipboard — try again";
        }
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveTo(_savedPath ?? _capture.BuildSavePath());

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        Markup.CommitPendingEdits();
        var suggested = _savedPath ?? _capture.BuildSavePath();
        var dialog = new SaveFileDialog
        {
            Filter = "PNG image|*.png",
            DefaultExt = ".png",
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.GetDirectoryName(suggested)
        };

        if (dialog.ShowDialog(this) == true)
            SaveTo(dialog.FileName);
    }

    private void SaveTo(string path)
    {
        Markup.CommitPendingEdits();
        try
        {
            CaptureService.SavePng(Flattened, path);
            _savedPath = path;
            _dirty = false;
            Status.Text = $"Saved to {path}";
        }
        catch (Exception ex)
        {
            App.LogError("EditorSave", ex);
            Status.Text = $"Could not save: {ex.Message}";
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        Markup.CommitPendingEdits();
        if (!HasUnsavedEdits)
            return;

        var choice = MessageBox.Show(this,
            "Your markup has not been copied or saved. Close anyway?",
            "HDRSnip", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        e.Cancel = choice != MessageBoxResult.Yes;
    }

    // ------------------------------------------------------------ tools

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        // The default tool's IsChecked fires mid-InitializeComponent, before the canvas exists.
        if (Markup is not null && sender is RadioButton { Tag: string tag })
            Markup.Tool = Enum.Parse<MarkupTool>(tag);
    }

    private void OnCanvasToolChanged()
    {
        if (_toolButtons.TryGetValue(Markup.Tool, out var button) && button.IsChecked != true)
            button.IsChecked = true;

        CropActions.Visibility = Markup.Tool == MarkupTool.Crop ? Visibility.Visible : Visibility.Collapsed;
        if (_zoom is null)
            ApplyZoom();   // the canvas grows to the full image while cropping
        SyncStyle();
        UpdateHint();
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Markup.Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Markup.Redo();

    private void OnClear(object sender, RoutedEventArgs e) => Markup.ClearMarks();

    private void OnApplyCrop(object sender, RoutedEventArgs e) => Markup.ApplyCrop();

    private void OnCancelCrop(object sender, RoutedEventArgs e) => Markup.Tool = MarkupTool.Select;

    private void OnHistoryChanged()
    {
        _flattened = null;
        _dirty = true;
        if (_zoom is null)
            ApplyZoom();   // a crop changes the content size
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        var doc = Markup.Document;
        UndoButton.IsEnabled = Markup.History.CanUndo;
        RedoButton.IsEnabled = Markup.History.CanRedo;
        ClearButton.IsEnabled = !doc.Annotations.IsEmpty;

        Dimensions.Text = $"{doc.Crop.Width} × {doc.Crop.Height}";
        EditedBadge.Visibility = doc.IsPristine(_result.Image) ? Visibility.Collapsed : Visibility.Visible;
        UpdateHint();
    }

    private void UpdateHint()
    {
        Hint.Text = Markup.IsEditingText
            ? "Type · Enter for a new line · Ctrl+Enter or click away to finish · Esc cancels"
            : Markup.Tool switch
            {
                MarkupTool.Select when Markup.Selected is not null =>
                    "Drag to move · arrow keys nudge · Delete removes · pick a colour or size to restyle",
                MarkupTool.Select => "Click a mark to select it · double-click text to edit",
                MarkupTool.Pen or MarkupTool.Highlighter => "Drag to draw · hold Shift for a straight line",
                MarkupTool.Line or MarkupTool.Arrow => "Drag to draw · hold Shift to snap to 45°",
                MarkupTool.Rectangle => "Drag to draw · hold Shift for a square",
                MarkupTool.Ellipse => "Drag to draw · hold Shift for a circle",
                MarkupTool.Text => "Click where the text should start",
                MarkupTool.Pixelate => "Drag over anything that should not be readable",
                MarkupTool.Crop => "Drag the handles or draw a new area · Enter applies · Esc cancels",
                _ => string.Empty
            };
    }

    // ------------------------------------------------------------ style flyout

    private void BuildSwatches()
    {
        var style = (Style)FindResource("Swatch");
        foreach (var (color, name) in Palette.Swatches)
        {
            var swatch = new RadioButton
            {
                Style = style,
                GroupName = "Swatch",
                Background = new SolidColorBrush(color),
                Tag = color,
                ToolTip = name
            };
            swatch.Checked += OnSwatchChecked;
            _swatches.Add(swatch);
            Swatches.Children.Add(swatch);
        }
    }

    private void OnOpenStyle(object sender, RoutedEventArgs e)
    {
        if (!Markup.HasStyle)
            return;

        SyncStyle();
        StyleFlyout.IsOpen = !StyleFlyout.IsOpen;
    }

    private void OnStyleFlyoutOpened(object sender, EventArgs e) => UpdateSizePreview();

    private void OnSwatchChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingStyle || sender is not RadioButton { Tag: Color color })
            return;

        Markup.Color = color;
        _flattened = null;
        SyncStyle();
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingStyle)
            return;

        Markup.Size = e.NewValue;
        _flattened = null;
        UpdateSizePreview();
        StyleSizeLabel.Text = SizeLabel(Markup.Size);
    }

    /// <summary>Pushes the active tool's colour and size into the chip and the flyout.</summary>
    private void SyncStyle()
    {
        _syncingStyle = true;
        try
        {
            bool enabled = Markup.HasStyle;
            StyleButton.IsEnabled = enabled;
            if (!enabled)
            {
                StyleFlyout.IsOpen = false;
                StyleDot.Fill = null;
                StyleSizeLabel.Text = "—";
                return;
            }

            var color = Markup.Color;
            StyleDot.Fill = new SolidColorBrush(color);
            StyleSizeLabel.Text = SizeLabel(Markup.Size);

            foreach (var swatch in _swatches)
                swatch.IsChecked = (Color)swatch.Tag == color;

            var (min, max) = Markup.SizeRange;
            SizeSlider.Minimum = min;
            SizeSlider.Maximum = max;
            SizeSlider.Value = Markup.Size;
            UpdateSizePreview();
        }
        finally
        {
            _syncingStyle = false;
        }
    }

    private void UpdateSizePreview()
    {
        var brush = new SolidColorBrush(Markup.Color);
        bool text = Markup.StyleTarget == MarkupTool.Text;

        SizePreviewText.Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        SizePreviewStroke.Visibility = text ? Visibility.Collapsed : Visibility.Visible;

        if (text)
        {
            SizePreviewText.Foreground = brush;
            SizePreviewText.FontSize = Math.Min(Markup.Size, 44);
        }
        else
        {
            SizePreviewStroke.Stroke = brush;
            SizePreviewStroke.StrokeThickness = Math.Min(Markup.Size, 40);
            SizePreviewStroke.Opacity = Markup.StyleTarget == MarkupTool.Highlighter ? 0.42 : 1;
        }
    }

    private static string SizeLabel(double size) => $"{size:0} px";

    /// <summary>Logical tree walk: complete right after InitializeComponent, unlike the visual tree.</summary>
    private static IEnumerable<RadioButton> FindToolButtons(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node)
                continue;
            if (node is RadioButton { GroupName: "Tool" } button)
                yield return button;
            foreach (var nested in FindToolButtons(node))
                yield return nested;
        }
    }

    // ------------------------------------------------------------ zoom

    private void OnToggleZoom(object sender, RoutedEventArgs e)
    {
        _zoom = _zoom is null ? 1 : null;
        ApplyZoom();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => StepZoom(1);

    private void OnZoomOut(object sender, RoutedEventArgs e) => StepZoom(-1);

    private void OnViewportWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        e.Handled = true;
        StepZoom(Math.Sign(e.Delta));
    }

    private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_zoom is null)
            ApplyZoom();
    }

    private void StepZoom(int direction)
    {
        double current = _zoom ?? FitScale();
        int index = Array.FindIndex(ZoomStops, stop => stop > current + 0.001);
        if (direction < 0)
            index = Array.FindLastIndex(ZoomStops, stop => stop < current - 0.001);

        if (index < 0)
            index = direction > 0 ? ZoomStops.Length - 1 : 0;

        _zoom = ZoomStops[index];
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_result is null)
            return;

        double scale = _zoom ?? FitScale();
        Markup.LayoutTransform = new ScaleTransform(scale, scale);
        Markup.ViewScale = scale;

        // Crisp pixels when peering in, smooth resampling when pulling back.
        RenderOptions.SetBitmapScalingMode(Markup,
            scale >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        ZoomLabel.Text = _zoom is null ? "Fit" : $"{Math.Round(scale * 100)}%";
    }

    /// <summary>Largest scale that fits the viewport, never enlarging past 1:1.</summary>
    private double FitScale()
    {
        const double padding = 56 + 2;
        double available = Math.Max(Viewport.ActualWidth - padding, 1);
        double availableHeight = Math.Max(Viewport.ActualHeight - padding, 1);

        return Math.Min(1.0, Math.Min(
            available / Math.Max(Markup.Width, 1),
            availableHeight / Math.Max(Markup.Height, 1)));
    }

    // ------------------------------------------------------------ keyboard

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

        // While typing a text mark, only chorded shortcuts may reach the window.
        if (Keyboard.FocusedElement is TextBox && !control)
            return;

        if (StyleFlyout.IsOpen && e.Key == Key.Escape)
        {
            StyleFlyout.IsOpen = false;
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.C when control:
                OnCopy(sender, e);
                break;
            case Key.S when control && shift:
                OnSaveAs(sender, e);
                break;
            case Key.S when control:
                OnSave(sender, e);
                break;
            case Key.Z when control && shift:
            case Key.Y when control:
                Markup.Redo();
                break;
            case Key.Z when control:
                Markup.Undo();
                break;
            case Key.D0 or Key.NumPad0 when control:
                OnToggleZoom(sender, e);
                break;
            case Key.OemPlus or Key.Add when control:
                StepZoom(1);
                break;
            case Key.OemMinus or Key.Subtract when control:
                StepZoom(-1);
                break;

            case Key.Delete or Key.Back when !control:
                Markup.DeleteSelected();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when !control && Markup.Selected is not null:
                double step = shift ? 10 : 1;
                Markup.NudgeSelected(e.Key switch
                {
                    Key.Left => new Vector(-step, 0),
                    Key.Right => new Vector(step, 0),
                    Key.Up => new Vector(0, -step),
                    _ => new Vector(0, step)
                });
                break;
            case Key.Enter when Markup.Tool == MarkupTool.Crop:
                Markup.ApplyCrop();
                break;

            case Key.V when !control && !alt: Markup.Tool = MarkupTool.Select; break;
            case Key.P when !control && !alt: Markup.Tool = MarkupTool.Pen; break;
            case Key.H when !control && !alt: Markup.Tool = MarkupTool.Highlighter; break;
            case Key.L when !control && !alt: Markup.Tool = MarkupTool.Line; break;
            case Key.A when !control && !alt: Markup.Tool = MarkupTool.Arrow; break;
            case Key.R when !control && !alt: Markup.Tool = MarkupTool.Rectangle; break;
            case Key.E when !control && !alt: Markup.Tool = MarkupTool.Ellipse; break;
            case Key.T when !control && !alt: Markup.Tool = MarkupTool.Text; break;
            case Key.X when !control && !alt: Markup.Tool = MarkupTool.Pixelate; break;
            case Key.C when !control && !alt: Markup.Tool = MarkupTool.Crop; break;
            case Key.S when !control && !alt: OnOpenStyle(sender, e); break;

            case Key.Escape:
                if (!Markup.Cancel())
                    Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
