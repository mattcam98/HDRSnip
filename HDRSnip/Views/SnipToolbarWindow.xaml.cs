using System.Windows;
using System.Windows.Input;
using HDRSnip.Models;

namespace HDRSnip.Views;

public partial class SnipToolbarWindow : Window
{
    public Models.CaptureMode? ChosenMode { get; private set; }
    public bool Cancelled { get; private set; } = true;

    public SnipToolbarWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = (wa.Width - ActualWidth) / 2 + wa.Left;
            Top = wa.Top + 16;
            Activate();
        };
    }

    private void OnRectangle(object sender, RoutedEventArgs e) => Finish(Models.CaptureMode.Rectangle);
    private void OnWindow(object sender, RoutedEventArgs e) => Finish(Models.CaptureMode.Window);
    private void OnFullScreen(object sender, RoutedEventArgs e) => Finish(Models.CaptureMode.FullScreen);

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Cancelled = true;
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled = true;
            Close();
        }
        else if (e.Key == Key.R)
            Finish(Models.CaptureMode.Rectangle);
        else if (e.Key == Key.W)
            Finish(Models.CaptureMode.Window);
        else if (e.Key == Key.F)
            Finish(Models.CaptureMode.FullScreen);
    }

    private void Finish(Models.CaptureMode mode)
    {
        ChosenMode = mode;
        Cancelled = false;
        Close();
    }
}
