using System.IO;
using System.Text.Json;

namespace HDRSnip.Models;

public enum ToneMapMethod
{
    Windows,
    Aces,
    Reinhard
}

public enum CaptureMode
{
    Rectangle,
    Window,
    FullScreen
}

public sealed class AppConfig
{
    public string SaveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDRSnip");

    public ToneMapMethod ToneMapMethod { get; set; } = ToneMapMethod.Windows;

    /// <summary>SDR paper-white in nits (Windows scRGB: 1.0 = 80 nits). Higher = darker output.</summary>
    public double SdrWhiteNits { get; set; } = 250;

    public bool CopyToClipboard { get; set; } = true;
    public bool OpenEditorAfterCapture { get; set; } = true;
    public bool AutoSave { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool PlaySound { get; set; } = false;

    /// <summary>Modifiers: Ctrl=2, Shift=4, Alt=1, Win=8. Default Ctrl+Shift+S.</summary>
    public uint RegionHotkeyModifiers { get; set; } = 6; // Ctrl+Shift
    public uint RegionHotkeyVk { get; set; } = 0x53; // S

    public uint FullScreenHotkeyModifiers { get; set; } = 6; // Ctrl+Shift
    public uint FullScreenHotkeyVk { get; set; } = 0x2C; // Print Screen

    private static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HDRSnip", "config.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
        }
        catch
        {
            // fall through to defaults
        }

        return new AppConfig();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
