using System.Windows;
using System.Windows.Input;
using HDRSnip.Models;

namespace HDRSnip.Views;

/// <summary>
/// The floating mode picker shown from the tray. Hotkeys skip it entirely and
/// go straight to their capture, so this only has to be quick to dismiss.
/// </summary>
public partial class ModeBarWindow : Window
{
    private bool _closing;

    /// <summary>Null when the user cancelled.</summary>
    public SnipMode? ChosenMode { get; private set; }

    public ModeBarWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Top + 8;
            Activate();
        };

        // Clicking away dismisses the bar. Closing also deactivates it, so this
        // has to be idempotent or WPF throws on the re-entrant Close.
        Deactivated += (_, _) => Dismiss();
    }

    private void OnRectangle(object sender, RoutedEventArgs e) => Choose(SnipMode.Rectangle);

    private void OnFullScreen(object sender, RoutedEventArgs e) => Choose(SnipMode.FullScreen);

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.R: Choose(SnipMode.Rectangle); break;
            case Key.F: Choose(SnipMode.FullScreen); break;
            case Key.Escape: Dismiss(); break;
        }
    }

    private void Choose(SnipMode mode)
    {
        ChosenMode = mode;
        Dismiss();
    }

    /// <summary>Set here rather than in Dismiss so it holds however the close was started.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    private void Dismiss()
    {
        if (_closing)
            return;

        Close();
    }
}
