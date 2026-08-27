using System.IO;
using System.Reflection;
using System.Windows;
using HDRSnip.Models;
using HDRSnip.Services;
using Microsoft.Win32;

namespace HDRSnip.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        SourceInitialized += (_, _) => ThemeService.ApplyToWindow(this);

        SaveFolderBox.Text = config.SaveFolder;
        CopyToggle.IsChecked = config.CopyToClipboard;
        AutoSaveToggle.IsChecked = config.AutoSave;
        EditorToggle.IsChecked = config.OpenEditorAfterCapture;
        AutostartToggle.IsChecked = config.StartWithWindows || AutostartService.IsEnabled();

        ToneMapBox.SelectedIndex = config.ToneMapMethod switch
        {
            ToneMapMethod.Aces => 1,
            ToneMapMethod.Reinhard => 2,
            _ => 0
        };

        NitsSlider.Value = config.SdrWhiteNits;
        NitsLabel.Text = $"{(int)config.SdrWhiteNits} nits";

        RegionHotkey.Hotkey = config.RegionHotkey;
        FullScreenHotkey.Hotkey = config.FullScreenHotkey;

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = $"HDRSnip {version?.ToString(3) ?? "1.0.0"}";
    }

    private void OnNitsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NitsLabel is not null)
            NitsLabel.Text = $"{(int)e.NewValue} nits";
    }

    /// <summary>SDR white only affects the Windows curve; the others derive exposure from the frame.</summary>
    private void OnCurveChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (NitsRow is not null)
            NitsRow.IsEnabled = ToneMapBox.SelectedIndex == 0;
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where captures are saved",
            InitialDirectory = Directory.Exists(SaveFolderBox.Text) ? SaveFolderBox.Text : null
        };

        if (dialog.ShowDialog(this) == true)
            SaveFolderBox.Text = dialog.FolderName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var folder = SaveFolderBox.Text.Trim();
        if (folder.Length == 0)
            folder = new AppConfig().SaveFolder;

        _config.SaveFolder = folder;
        _config.CopyToClipboard = CopyToggle.IsChecked == true;
        _config.AutoSave = AutoSaveToggle.IsChecked == true;
        _config.OpenEditorAfterCapture = EditorToggle.IsChecked == true;
        _config.StartWithWindows = AutostartToggle.IsChecked == true;
        _config.SdrWhiteNits = NitsSlider.Value;
        _config.ToneMapMethod = ToneMapBox.SelectedIndex switch
        {
            1 => ToneMapMethod.Aces,
            2 => ToneMapMethod.Reinhard,
            _ => ToneMapMethod.Windows
        };
        _config.RegionHotkey = RegionHotkey.Hotkey;
        _config.FullScreenHotkey = FullScreenHotkey.Hotkey;

        try
        {
            _config.Save();
        }
        catch (Exception ex)
        {
            App.LogError("SaveSettings", ex);
            MessageBox.Show($"Settings could not be written to disk.\n\n{ex.Message}",
                "HDRSnip", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        AutostartService.SetEnabled(_config.StartWithWindows);
        DialogResult = true;
        Close();
    }
}
