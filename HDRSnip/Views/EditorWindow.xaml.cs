using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using HDRSnip.Editing;
using HDRSnip.Models;
using HDRSnip.Services;
using Microsoft.Win32;

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
    private readonly List<RadioButton> _toneButtons;
    private readonly List<RadioButton> _redactButtons;
    private CaptureResult _result = null!;
    private string? _savedPath;
    private double? _zoom;          // null means fit to viewport
    private bool _dirty;            // edits since the last copy or save
    private bool _syncingStyle;
    private bool _syncingTone;
    private ToneMapMethod _toneMethod;
    private double _toneWhite;
    private BitmapSource? _flattened;

    public EditorWindow(CaptureResult result, CaptureService capture)
    {
        InitializeComponent();
        _capture = capture;

        BuildSwatches();
        foreach (var button in FindGroup(this, "Tool"))
            _toolButtons[Enum.Parse<MarkupTool>((string)button.Tag)] = button;
        _toneButtons = FindGroup(ToneFlyout.Child, "Tone").ToList();
        _redactButtons = FindGroup(StyleFlyout.Child, "Redact").ToList();

        Markup.History.Changed += OnHistoryChanged;
        Markup.SelectionChanged += SyncStyle;
        Markup.ToolChanged += OnCanvasToolChanged;
        Markup.ColorPicked += OnColorPicked;
        Markup.TextEditingChanged += UpdateHint;

        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);
        DpiChanged += (_, _) => ApplyZoom();
        Closed += (_, _) => App.TrimMemoryWhenIdle();
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
        HdrBadge.Visibility = result.Frame is not null ? Visibility.Visible : Visibility.Collapsed;
        ToneFlyout.IsOpen = false;
        if (result.Frame is { } frame)
        {
            _toneMethod = _capture.ToneMapMethod;
            _toneWhite = _capture.WhiteLevelFor(frame);
            SyncTone();
        }

        _zoom = null;
        ApplyZoom();
        UpdateChrome();
        SyncStyle();

        Status.Text = _savedPath is not null ? $"Saved to {_savedPath}"
            : result.SaveFailed ? "Auto-save failed — check the save folder, or use Save as"
            : "Copied to clipboard";
    }

    public void ShowStatus(string text) => Status.Text = text;

    /// <summary>The image as the user sees it: source plus marks, trimmed to the crop.</summary>
    private BitmapSource Flattened => _flattened ??= Markup.Flatten();

    // ------------------------------------------------------------ export

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        Markup.CommitPendingEdits();
        if (ClipboardService.TryCopy(Flattened))
        {
            _dirty = false;
            Status.Text = "Copied to clipboard";
        }
        else
        {
            Status.Text = "Another app is holding the clipboard — try again";
        }
    }

    private async void OnCopyText(object sender, RoutedEventArgs e)
    {
        Markup.CommitPendingEdits();
        CopyTextButton.IsEnabled = false;
        try
        {
            // The capture as cropped, without marks: a redaction box is not text to read.
            var crop = Markup.Document.Crop;
            BitmapSource image = Markup.Document.IsCropped(_result.Image)
                ? new CroppedBitmap(_result.Image, crop)
                : _result.Image;

            string? text = await TextRecognizer.RecognizeAsync(image);
            if (text is null)
            {
                Status.Text = "Windows has no text-recognition language installed";
            }
            else if (text.Length == 0)
            {
                Status.Text = "No text found";
            }
            else
            {
                Clipboard.SetText(text);
                int lines = text.Split('\n').Length;
                Status.Text = lines == 1 ? "Copied 1 line of text" : $"Copied {lines} lines of text";
            }
        }
        catch (Exception ex)
        {
            App.LogError("Ocr", ex);
            Status.Text = $"Could not read text: {ex.Message}";
        }
        finally
        {
            CopyTextButton.IsEnabled = true;
        }
    }

    private void OnColorPicked(Color color)
    {
        string hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        try
        {
            Clipboard.SetText(hex);
            Status.Text = $"Copied {hex}";
        }
        catch (Exception ex)
        {
            App.LogError("Eyedropper", ex);
            Status.Text = $"{hex} — another app is holding the clipboard";
        }
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        Markup.CommitPendingEdits();
        new PinWindow(Flattened).Show();
    }

    private void OnSave(object sender, RoutedEventArgs e) => SaveTo(_savedPath);

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        Markup.CommitPendingEdits();
        var dialog = new SaveFileDialog
        {
            Filter = _result.Frame is null
                ? "PNG image|*.png"
                : "PNG image|*.png|JPEG XR HDR image|*.jxr",
            DefaultExt = ".png",
            FileName = _savedPath is null ? $"HDRSnip_{DateTime.Now:yyyyMMdd_HHmmss}.png" : Path.GetFileName(_savedPath),
            InitialDirectory = Path.GetDirectoryName(_savedPath) ?? App.Config.SaveFolder
        };

        if (dialog.ShowDialog(this) == true)
            SaveTo(dialog.FileName);
    }

    /// <summary>Saves to <paramref name="path"/>, or to a new file in the captures folder when it is null.</summary>
    private void SaveTo(string? path)
    {
        Markup.CommitPendingEdits();
        try
        {
            path ??= _capture.BuildSavePath();
            if (_result.Frame is { } frame && Path.GetExtension(path).Equals(".jxr", StringComparison.OrdinalIgnoreCase))
            {
                // The float pixels as captured, not the tone-mapped preview; marks sit at SDR white.
                var document = Markup.Document;
                ImageCodec.SaveHdr(frame, document.Crop, document.RenderMarks(_result.Image), _capture.WhiteLevelFor(frame), path);
                App.TrimMemoryWhenIdle();
            }
            else
            {
                ImageCodec.Save(Flattened, path);
            }

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

    // ------------------------------------------------------------ tone mapping

    private void OnOpenTone(object sender, RoutedEventArgs e) => ToneFlyout.IsOpen = !ToneFlyout.IsOpen;

    private void OnToneChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTone || sender is not RadioButton { Tag: string tag })
            return;

        _toneMethod = Enum.Parse<ToneMapMethod>(tag);
        ApplyTone();
    }

    private void OnToneWhiteChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingTone)
            return;

        _toneWhite = e.NewValue;
        ApplyTone();
    }

    /// <summary>Re-renders the capture from its float pixels; the marks and history are untouched.</summary>
    private void ApplyTone()
    {
        // The slider reports its initial value while the window is still being built.
        if (_result?.Frame is not { } frame)
            return;

        var image = CaptureService.ToneMap(frame, _toneMethod, _toneWhite);
        _result = _result with { Image = image, Png = null };
        Markup.ReplaceSource(image);
        _flattened = null;
        _dirty = true;
        SyncTone();
    }

    private void SyncTone()
    {
        _syncingTone = true;
        try
        {
            foreach (var choice in _toneButtons)
                choice.IsChecked = (string)choice.Tag == _toneMethod.ToString();

            // Only the Windows curve takes a white level; the others derive exposure from the frame.
            ToneWhitePanel.Visibility = _toneMethod == ToneMapMethod.Windows ? Visibility.Visible : Visibility.Collapsed;
            ToneWhiteSlider.Value = _toneWhite;
            ToneWhiteLabel.Text = $"{_toneWhite:0} nits";
        }
        finally
        {
            _syncingTone = false;
        }
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
                    "Drag to move · drag a handle to reshape · arrow keys nudge · Delete removes",
                MarkupTool.Select => "Click a mark to select it · double-click text to edit",
                MarkupTool.Pen or MarkupTool.Highlighter => "Drag to draw · hold Shift for a straight line",
                MarkupTool.Line or MarkupTool.Arrow => "Drag to draw · hold Shift to snap to 45°",
                MarkupTool.Rectangle => "Drag to draw · hold Shift for a square",
                MarkupTool.Ellipse => "Drag to draw · hold Shift for a circle",
                MarkupTool.Text => "Click where the text should start",
                MarkupTool.Number => "Click to place the next number",
                MarkupTool.Pixelate => "Drag over anything that should not be readable",
                MarkupTool.Spotlight => "Drag over the area to keep bright · everything else dims",
                MarkupTool.Eyedropper => "Click a pixel to copy its colour as hex",
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

    private void OnFillChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingStyle)
            return;

        Markup.Fill = FillToggle.IsChecked == true;
        _flattened = null;
    }

    private void OnRedactChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingStyle || sender is not RadioButton { Tag: string tag })
            return;

        Markup.RedactMode = Enum.Parse<RedactMode>(tag);
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
            StyleDot.Fill = Markup.HasColor ? new SolidColorBrush(color) : null;
            StyleSizeLabel.Text = Markup.IsRedaction ? Markup.RedactMode.ToString() : SizeLabel(Markup.Size);

            RedactPanel.Visibility = Markup.IsRedaction ? Visibility.Visible : Visibility.Collapsed;
            ColorPanel.Visibility = Markup.HasColor ? Visibility.Visible : Visibility.Collapsed;
            SizePanel.Visibility = Markup.HasSize ? Visibility.Visible : Visibility.Collapsed;
            FillToggle.Visibility = Markup.CanFill ? Visibility.Visible : Visibility.Collapsed;
            FillToggle.Content = Markup.StyleTarget == MarkupTool.Text ? "Background" : "Fill";
            FillToggle.IsChecked = Markup.Fill;

            foreach (var choice in _redactButtons)
                choice.IsChecked = (string)choice.Tag == Markup.RedactMode.ToString();

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
        bool text = Markup.StyleTarget is MarkupTool.Text or MarkupTool.Number;

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
    private static IEnumerable<RadioButton> FindGroup(DependencyObject root, string group)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node)
                continue;
            if (node is RadioButton button && button.GroupName == group)
                yield return button;
            foreach (var nested in FindGroup(node, group))
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

    /// <summary>
    /// Zoom is in screen pixels per image pixel, so 100% is pixel-exact on a
    /// scaled display too; the canvas itself lays out in DIPs.
    /// </summary>
    private void ApplyZoom()
    {
        if (_result is null)
            return;

        double zoom = _zoom ?? FitScale();
        double scale = zoom / VisualTreeHelper.GetDpi(this).DpiScaleX;
        Markup.LayoutTransform = new ScaleTransform(scale, scale);
        Markup.ViewScale = scale;

        // Crisp pixels when peering in, smooth resampling when pulling back.
        RenderOptions.SetBitmapScalingMode(Markup,
            zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        ZoomLabel.Text = _zoom is null ? "Fit" : $"{Math.Round(zoom * 100)}%";
    }

    /// <summary>Largest zoom that fits the viewport, never enlarging past 1:1.</summary>
    private double FitScale()
    {
        const double padding = 56 + 2;
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double width = Math.Max(Viewport.ActualWidth - padding, 1) * dpi;
        double height = Math.Max(Viewport.ActualHeight - padding, 1) * dpi;

        return Math.Min(1.0, Math.Min(
            width / Math.Max(Markup.Width, 1),
            height / Math.Max(Markup.Height, 1)));
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
            case Key.N when !control && !alt: Markup.Tool = MarkupTool.Number; break;
            case Key.X when !control && !alt: Markup.Tool = MarkupTool.Pixelate; break;
            case Key.F when !control && !alt: Markup.Tool = MarkupTool.Spotlight; break;
            case Key.I when !control && !alt: Markup.Tool = MarkupTool.Eyedropper; break;
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
