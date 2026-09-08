using System.Windows.Media;

namespace HDRSnip.Editing;

/// <summary>Twelve swatches that read on both dark and light captures.</summary>
public static class Palette
{
    public static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);
    public static readonly Color Yellow = Color.FromRgb(0xFF, 0xD6, 0x0A);

    public static readonly IReadOnlyList<(Color Color, string Name)> Swatches =
    [
        (Color.FromRgb(0x14, 0x14, 0x1A), "Black"),
        (Color.FromRgb(0xFF, 0xFF, 0xFF), "White"),
        (Color.FromRgb(0x6F, 0x72, 0x7C), "Grey"),
        (Red, "Red"),
        (Color.FromRgb(0xF7, 0x6B, 0x15), "Orange"),
        (Yellow, "Yellow"),
        (Color.FromRgb(0x30, 0xA4, 0x6C), "Green"),
        (Color.FromRgb(0x12, 0xA5, 0x94), "Teal"),
        (Color.FromRgb(0x00, 0x91, 0xFF), "Blue"),
        (Color.FromRgb(0x6E, 0x56, 0xCF), "Violet"),
        (Color.FromRgb(0xE9, 0x3D, 0x82), "Pink"),
        (Color.FromRgb(0x8B, 0x5A, 0x2B), "Brown"),
    ];
}
