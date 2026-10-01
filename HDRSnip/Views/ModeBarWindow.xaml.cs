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
    private static readonly int[] DelayStops = [0, 3, 5, 10];

    /// <summary>Remembered for the session: a delay is usually wanted several snips in a row.</summary>
    private static int _delaySeconds;

    private bool _closing;

    /// <summary>Null when the user cancelled.</summary>
    public SnipMode? ChosenMode { get; private set; }

    /// <summary>Seconds to wait before capturing; zero for none.</summary>
    public int DelaySeconds => _delaySeconds;

    public ModeBarWindow()
    {
        InitializeComponent();
        UpdateDelayLabel();

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

    private void OnDelay(object sender, RoutedEventArgs e) => CycleDelay();

    private void OnClose(object sender, RoutedEventArgs e) => Dismiss();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.R: Choose(SnipMode.Rectangle); break;
            case Key.F: Choose(SnipMode.FullScreen); break;
            case Key.D: CycleDelay(); break;
            case Key.Escape: Dismiss(); break;
        }
    }

    private void CycleDelay()
    {
        _delaySeconds = DelayStops[(Array.IndexOf(DelayStops, _delaySeconds) + 1) % DelayStops.Length];
        UpdateDelayLabel();
    }

    private void UpdateDelayLabel() =>
        DelayLabel.Text = _delaySeconds == 0 ? "No delay" : $"{_delaySeconds} s delay";

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
