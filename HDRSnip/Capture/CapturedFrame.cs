using System.Drawing;

namespace HDRSnip.Capture;

public sealed class MonitorInfo
{
    public required int OutputIndex { get; init; }
    public required Rectangle Bounds { get; init; }
    public required bool IsHdr { get; init; }
}

/// <summary>
/// One captured desktop image.
/// </summary>
/// <remarks>
/// Pixels stay in the GPU's native half-float format all the way from the DXGI
/// staging texture to the tone mapper. Expanding to <c>float</c> would double
/// every allocation and every byte crossing the daemon boundary for no added
/// precision — the source is 16-bit either way.
/// </remarks>
public sealed class CapturedFrame
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
}
