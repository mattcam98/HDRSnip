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

    /// <summary>
    /// When true, open the editor immediately. When false (default), copy + show a toast;
    /// clicking the toast opens the editor.
    /// </summary>
    public bool OpenEditorAfterCapture { get; set; } = false;

    public bool AutoSave { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool PlaySound { get; set; } = false;

    /// <summary>Bumped when defaults change so existing installs pick up new UX.</summary>
    public int ConfigVersion { get; set; } = 2;

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
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                Migrate(cfg);
                return cfg;
            }
        }
        catch
        {
            // fall through to defaults
        }

        return new AppConfig();
    }

    private static void Migrate(AppConfig cfg)
    {
        // v2: notification-first capture (don't auto-open editor).
        if (cfg.ConfigVersion < 2)
        {
            cfg.OpenEditorAfterCapture = false;
            cfg.ConfigVersion = 2;
            try { cfg.Save(); } catch { /* ignore */ }
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
