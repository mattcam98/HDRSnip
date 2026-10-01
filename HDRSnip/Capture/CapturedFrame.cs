using System.Drawing;

namespace HDRSnip.Capture;

/// <param name="DeviceName">GDI name of the output (<c>\.\DISPLAY1</c>), the key Windows display settings use.</param>
public sealed record MonitorInfo(int OutputIndex, Rectangle Bounds, bool IsHdr, string DeviceName);

/// <summary>
/// One captured desktop image, kept in the GPU's native half-float format all
/// the way from the DXGI staging texture to the tone mapper.
/// </summary>
public sealed record CapturedFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Rectangle MonitorBounds { get; init; }
    public required bool WasHdr { get; init; }

    /// <summary>
    /// True for DXGI captures (linear scRGB, 1.0 = 80 nits).
    /// False for the GDI fallback, which is already display-referred sRGB in [0,1].
    /// </summary>
    public required bool IsLinearScRgb { get; init; }

    /// <summary>Interleaved RGBA, <c>Width * Height * 4</c> half-floats.</summary>
    public required Half[] Rgba { get; init; }

    /// <summary>
    /// The monitor's Windows "SDR content brightness" at capture time, in nits,
    /// when it could be read. This is the level SDR white was composited at.
    /// </summary>
    public double? SdrWhiteNits { get; init; }

    /// <summary>The mouse pointer to draw over the tone-mapped image, when cursor capture is on.</summary>
    public CursorImage? Cursor { get; init; }
}
