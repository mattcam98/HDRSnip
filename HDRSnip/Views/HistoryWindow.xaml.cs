using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using HDRSnip.Services;

namespace HDRSnip.Views;

public enum HistoryAction
{
    Edit,
    Copy,
    Pin
}

/// <summary>
/// Floating strip of this session's captures, shown from the tray. Click one to
/// open it in the editor, or use the buttons beneath it to copy or pin it.
/// </summary>
public partial class HistoryWindow : Window
{
    private bool _closing;

    /// <summary>Null when the user dismissed the window without choosing.</summary>
    public (CaptureHistory.Entry Entry, HistoryAction Action)? Choice { get; private set; }

    public HistoryWindow(CaptureHistory history)
    {
        InitializeComponent();

        foreach (var entry in history.Entries)
            Items.Children.Add(BuildItem(entry));

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Top + 8;
            Activate();
        };

        // Clicking away dismisses it. Closing also deactivates, so guard the re-entry.
        Deactivated += (_, _) => Dismiss();
    }

    private StackPanel BuildItem(CaptureHistory.Entry entry)
    {
        var open = new Button
        {
            Style = (Style)FindResource("Button.Subtle"),
            Padding = new Thickness(6),
            ToolTip = "Open in the editor",
            Content = new Image
            {
                Source = entry.Thumbnail,
                Width = 180,
                Height = 110,
                Stretch = System.Windows.Media.Stretch.Uniform
            }
        };
        open.Click += (_, _) => Choose(entry, HistoryAction.Edit);

        var caption = new TextBlock
        {
            Style = (Style)FindResource("Type.Caption"),
            Text = $"{entry.Width} × {entry.Height} · {entry.Time:HH:mm:ss}",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        var actions = new DockPanel();
        var pin = IconButton("", "Pin to screen", () => Choose(entry, HistoryAction.Pin));
        var copy = IconButton("", "Copy", () => Choose(entry, HistoryAction.Copy));
        DockPanel.SetDock(pin, Dock.Right);
        DockPanel.SetDock(copy, Dock.Right);
        actions.Children.Add(pin);
        actions.Children.Add(copy);
        actions.Children.Add(caption);

        return new StackPanel { Margin = new Thickness(2), Children = { open, actions } };
    }

    private Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("Button.Icon"),
            Width = 28,
            Height = 28,
            Padding = new Thickness(0),
            ToolTip = tip,
            Content = new TextBlock { Style = (Style)FindResource("Glyph"), Text = glyph, FontSize = 12 }
        };
        button.Click += (_, _) => click();
        return button;
    }

    private void Choose(CaptureHistory.Entry entry, HistoryAction action)
    {
        Choice = (entry, action);
        Dismiss();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            Dismiss();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Dismiss()
    {
        if (!_closing)
            Close();
    }
}
