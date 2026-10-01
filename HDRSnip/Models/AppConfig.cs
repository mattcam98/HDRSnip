using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HDRSnip.Models;

public enum ToneMapMethod
{
    Windows,
    Aces,
    Reinhard
}

public enum SnipMode
{
    Rectangle,
    FullScreen
}

/// <summary>
/// User settings, persisted as JSON under %LOCALAPPDATA%\HDRSnip.
/// Hotkeys stay as flat scalars on disk so configs written by earlier versions
/// keep loading; the rest of the app sees them through <see cref="Hotkey"/>.
/// </summary>
public sealed class AppConfig
{
    private const int CurrentVersion = 4;
    private const double DefaultSdrWhiteNits = 250;

    public static string DefaultSaveFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "HDRSnip");

    public string SaveFolder { get; set; } = DefaultSaveFolder;

    public ToneMapMethod ToneMapMethod { get; set; } = ToneMapMethod.Windows;

    /// <summary>SDR paper white in nits (scRGB: 1.0 = 80 nits). Higher means a darker screenshot.</summary>
    public double SdrWhiteNits { get; set; } = DefaultSdrWhiteNits;

    /// <summary>Use the monitor's own Windows "SDR content brightness" instead of <see cref="SdrWhiteNits"/>.</summary>
    public bool AutoSdrWhite { get; set; } = true;

    public bool CopyToClipboard { get; set; } = true;

    /// <summary>Open the editor straight away instead of showing the toast.</summary>
    public bool OpenEditorAfterCapture { get; set; }

    public bool AutoSave { get; set; }

    public bool StartWithWindows { get; set; }

    public int ConfigVersion { get; set; } = CurrentVersion;

    public uint RegionHotkeyModifiers { get; set; } = (uint)(HotkeyModifiers.Control | HotkeyModifiers.Shift);
    public uint RegionHotkeyVk { get; set; } = 0x53; // S

    public uint FullScreenHotkeyModifiers { get; set; } = (uint)(HotkeyModifiers.Control | HotkeyModifiers.Shift);
    public uint FullScreenHotkeyVk { get; set; } = 0x2C; // Print Screen

    [JsonIgnore]
    public Hotkey RegionHotkey
    {
        get => new((HotkeyModifiers)RegionHotkeyModifiers, RegionHotkeyVk);
        set
        {
            RegionHotkeyModifiers = (uint)value.Modifiers;
            RegionHotkeyVk = value.VirtualKey;
        }
    }

    [JsonIgnore]
    public Hotkey FullScreenHotkey
    {
        get => new((HotkeyModifiers)FullScreenHotkeyModifiers, FullScreenHotkeyVk);
        set
        {
            FullScreenHotkeyModifiers = (uint)value.Modifiers;
            FullScreenHotkeyVk = value.VirtualKey;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ConfigPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HDRSnip",
        "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions);
                if (config is not null)
                {
                    config.Repair();
                    config.Migrate();
                    return config;
                }
            }
        }
        catch
        {
            // A corrupt file should never stop the app starting.
        }

        return new AppConfig();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(directory);

        // Write-then-replace so a crash mid-write cannot leave an unreadable config.
        var temporary = ConfigPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, ConfigPath, overwrite: true);
    }

    /// <summary>A hand-edited file can parse and still hold values nothing downstream can use.</summary>
    private void Repair()
    {
        if (string.IsNullOrWhiteSpace(SaveFolder))
            SaveFolder = DefaultSaveFolder;
        if (!double.IsFinite(SdrWhiteNits) || SdrWhiteNits <= 0)
            SdrWhiteNits = DefaultSdrWhiteNits;
    }

    private void Migrate()
    {
        if (ConfigVersion >= CurrentVersion)
            return;

        // v2 moved to a notification-first flow rather than auto-opening the editor.
        if (ConfigVersion < 2)
            OpenEditorAfterCapture = false;

        // v4 added automatic SDR white. A level someone tuned by hand stays in charge.
        if (ConfigVersion < 4)
            AutoSdrWhite = SdrWhiteNits == DefaultSdrWhiteNits;

        ConfigVersion = CurrentVersion;
        try { Save(); } catch { /* settings still work in memory */ }
    }
}
