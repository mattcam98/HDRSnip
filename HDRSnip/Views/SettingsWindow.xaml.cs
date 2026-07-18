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

        SaveFolderBox.Text = config.SaveFolder;
        NitsSlider.Value = config.SdrWhiteNits;
        CopyCheck.IsChecked = config.CopyToClipboard;
        EditorCheck.IsChecked = config.OpenEditorAfterCapture;
        AutoSaveCheck.IsChecked = config.AutoSave;
        AutostartCheck.IsChecked = config.StartWithWindows || AutostartService.IsEnabled();

        ToneMapBox.SelectedIndex = config.ToneMapMethod switch
        {
            ToneMapMethod.Aces => 1,
            ToneMapMethod.Reinhard => 2,
            _ => 0
        };

        HotkeyInfo.Text =
            $"Rectangular: {HotkeyText.Format(config.RegionHotkeyModifiers, config.RegionHotkeyVk)}\n" +
            $"Fullscreen: {HotkeyText.Format(config.FullScreenHotkeyModifiers, config.FullScreenHotkeyVk)}";
    }

    private void OnNitsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (NitsLabel is not null)
            NitsLabel.Text = $"{(int)e.NewValue}";
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Screenshot save folder" };
        if (dlg.ShowDialog(this) == true)
            SaveFolderBox.Text = dlg.FolderName;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _config.SaveFolder = SaveFolderBox.Text.Trim();
        _config.SdrWhiteNits = NitsSlider.Value;
        _config.CopyToClipboard = CopyCheck.IsChecked == true;
        _config.OpenEditorAfterCapture = EditorCheck.IsChecked == true;
        _config.AutoSave = AutoSaveCheck.IsChecked == true;
        _config.StartWithWindows = AutostartCheck.IsChecked == true;
        _config.ToneMapMethod = ToneMapBox.SelectedIndex switch
        {
            1 => ToneMapMethod.Aces,
            2 => ToneMapMethod.Reinhard,
            _ => ToneMapMethod.Windows
        };

        _config.Save();
        AutostartService.SetEnabled(_config.StartWithWindows);
        DialogResult = true;
        Close();
    }
}
