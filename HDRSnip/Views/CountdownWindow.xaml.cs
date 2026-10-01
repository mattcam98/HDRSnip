using System.Windows;
using System.Windows.Interop;
using HDRSnip.Interop;

namespace HDRSnip.Views;

/// <summary>
/// Counts down to a delayed snip. It never takes focus or mouse input, so the
/// menu or hover state the delay exists to capture stays open underneath it.
/// </summary>
public partial class CountdownWindow : Window
{
    private CountdownWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            var work = SystemParameters.WorkArea;
            Left = work.Left + (work.Width - ActualWidth) / 2;
            Top = work.Top + 8;
        };
    }

    /// <summary>Shows the countdown and completes once it has left the screen.</summary>
    public static async Task RunAsync(int seconds)
    {
        var window = new CountdownWindow();
        try
        {
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                window.Label.Text = $"Capturing in {remaining}";
                if (!window.IsVisible)
                    window.Show();
                await Task.Delay(1000);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
