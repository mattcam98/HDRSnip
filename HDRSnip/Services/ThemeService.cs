using System.Windows;
using System.Windows.Interop;
using HDRSnip.Interop;
using Microsoft.Win32;

namespace HDRSnip.Services;

/// <summary>
/// Keeps the app palette and window chrome in step with the Windows app theme.
/// The palette lives at a fixed slot in <see cref="Application.Resources"/> so
/// swapping it retints everything bound with <c>DynamicResource</c>.
/// </summary>
public static class ThemeService
{
    private const int PaletteSlot = 0;

    private static readonly Uri DarkPalette = new("Theme/Dark.xaml", UriKind.Relative);
    private static readonly Uri LightPalette = new("Theme/Light.xaml", UriKind.Relative);

    public static bool IsDark { get; private set; } = true;

    public static void Initialize()
    {
        Apply(ReadSystemPrefersDark());
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Call once per window after the HWND exists to theme its title bar.</summary>
    public static void ApplyToWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            Native.ApplyWindowTheme(hwnd, IsDark);
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
            return;

        bool dark = ReadSystemPrefersDark();
        if (dark == IsDark)
            return;

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Apply(dark);
            foreach (Window window in Application.Current.Windows)
                ApplyToWindow(window);
        });
    }

    private static void Apply(bool dark)
    {
        IsDark = dark;
        var app = Application.Current;
        if (app is null)
            return;

        var palette = new ResourceDictionary { Source = dark ? DarkPalette : LightPalette };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > PaletteSlot)
            merged[PaletteSlot] = palette;
        else
            merged.Insert(PaletteSlot, palette);
    }

    private static bool ReadSystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // AppsUseLightTheme: 0 = dark, 1 = light. Absent means light on old builds.
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return true;
        }
    }
}
