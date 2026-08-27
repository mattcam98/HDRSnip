using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Models;

namespace HDRSnip.Capture;

/// <summary>
/// HDR to SDR conversion for scRGB linear frames (1.0 = 80 nits).
/// </summary>
/// <remarks>
/// Every supported curve is a pure per-channel function of the input sample, and
/// every input is one of 65,536 possible half-float bit patterns. So each curve
/// collapses into a 64 KB lookup table built once per capture; the pixel loop is
/// then three table reads per pixel with no transcendental maths at all. A 4K
/// frame maps in a few milliseconds instead of hundreds.
/// </remarks>
public static class ToneMapper
{
    /// <summary>Toast inline image — small and quick to encode.</summary>
    public const int ToastPreviewMaxEdge = 720;

    /// <summary>Below this, thread coordination costs more than it saves.</summary>
    private const int ParallelRowThreshold = 200_000;

    private const int LutSize = 65_536;

    // Half bit patterns are monotonic for positive finite values, so an integer
    // compare is enough to spot out-of-range (HDR) samples.
    private static readonly ushort HdrThresholdBits = BitConverter.HalfToUInt16Bits((Half)1.02f);
    private const ushort PositiveInfinityBits = 0x7C00;

    private static readonly object LutGate = new();
    private static byte[]? _cachedLut;
    private static (ToneMapMethod Method, double Param) _cachedKey = (ToneMapMethod.Windows, double.NaN);

    public static WriteableBitmap ToSdrBitmap(CapturedFrame frame, ToneMapMethod method, double sdrWhiteNits, double dpi)
    {
        var lut = ResolveLut(frame, method, sdrWhiteNits);

        var pixels = new byte[frame.Width * frame.Height * 4];
        MapToBgra8(frame.Rgba, pixels, frame.Width, frame.Height, lut);

        if (dpi < 1) dpi = 96;
        var bitmap = new WriteableBitmap(frame.Width, frame.Height, dpi, dpi, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), pixels, frame.Width * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }

    public static WriteableBitmap ScaleBitmapMaxEdge(BitmapSource source, int maxEdge)
    {
        int w = source.PixelWidth;
        int h = source.PixelHeight;
        if (maxEdge <= 0 || Math.Max(w, h) <= maxEdge)
            return source as WriteableBitmap ?? Freeze(new WriteableBitmap(source));

        double scale = maxEdge / (double)Math.Max(w, h);
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return Freeze(new WriteableBitmap(scaled));
    }

    private static WriteableBitmap Freeze(WriteableBitmap bitmap)
    {
        bitmap.Freeze();
        return bitmap;
    }

    // ------------------------------------------------------------- curve choice

    private static byte[] ResolveLut(CapturedFrame frame, ToneMapMethod method, double sdrWhiteNits)
    {
        // The GDI fallback is already display-referred sRGB — only quantise it.
        if (!frame.IsLinearScRgb)
            return GetCached(ToneMapMethod.Windows, double.NegativeInfinity, static v => v);

        // On an HDR output always apply the chosen curve, even when the peak
        // happens to sit below 1.0 (typical for a desktop of UI chrome).
        // On an SDR output there is nothing to compress, so only gamma-encode.
        if (!frame.WasHdr && !HasHdrPeak(frame.Rgba))
            return GetCached(ToneMapMethod.Windows, double.PositiveInfinity, static v => LinearToSrgb(Saturate(v)));

        switch (method)
        {
            case ToneMapMethod.Aces:
            {
                float exposure = AcesExposure(frame.Rgba);
                return BuildLut(v => LinearToSrgb(AcesCurve(v * exposure)));
            }

            case ToneMapMethod.Reinhard:
            {
                float scale = ReinhardScale(frame.Rgba);
                return BuildLut(v =>
                {
                    float x = Math.Max(v, 0f) * scale;
                    return LinearToSrgb(Saturate(x / (1f + x)));
                });
            }

            default:
            {
                // Windows / OBS: divide by paper white, clip, encode.
                float scale = Math.Max((float)(sdrWhiteNits / 80.0), 0.01f);
                return GetCached(ToneMapMethod.Windows, sdrWhiteNits, v => LinearToSrgb(Saturate(v / scale)));
            }
        }
    }

    /// <summary>Caches the curve for the parameter-stable methods so repeat captures skip the rebuild.</summary>
    private static byte[] GetCached(ToneMapMethod method, double param, Func<float, float> transfer)
    {
        lock (LutGate)
        {
            if (_cachedLut is not null && _cachedKey == (method, param))
                return _cachedLut;

            _cachedLut = BuildLut(transfer);
            _cachedKey = (method, param);
            return _cachedLut;
        }
    }

    private static byte[] BuildLut(Func<float, float> transfer)
    {
        var lut = new byte[LutSize];
        for (int bits = 0; bits < LutSize; bits++)
        {
            float input = (float)BitConverter.UInt16BitsToHalf((ushort)bits);
            if (float.IsNaN(input))
                input = 0f;
            else if (float.IsInfinity(input))
                input = input > 0 ? 65504f : 0f;

            float output = transfer(input);
            if (!float.IsFinite(output))
                output = 0f;

            lut[bits] = (byte)Math.Clamp((int)(output * 255f + 0.5f), 0, 255);
        }

        return lut;
    }

    // ------------------------------------------------------------- pixel loop

    private static unsafe void MapToBgra8(Half[] source, byte[] destination, int width, int height, byte[] lut)
    {
        fixed (Half* srcBase = source)
        fixed (byte* dstBase = destination)
        fixed (byte* lutBase = lut)
        {
            // Pointers cannot be captured by a lambda; integers can.
            nint src = (nint)srcBase;
            nint dst = (nint)dstBase;
            nint table = (nint)lutBase;
            int stride = width * 4;

            if ((long)width * height >= ParallelRowThreshold)
                Parallel.For(0, height, y => MapRow(src, dst, table, y, stride));
            else
                for (int y = 0; y < height; y++)
                    MapRow(src, dst, table, y, stride);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void MapRow(nint source, nint destination, nint table, int row, int stride)
    {
        ushort* src = (ushort*)source + (nint)row * stride;
        byte* dst = (byte*)destination + (nint)row * stride;
        byte* lut = (byte*)table;

        for (int i = 0; i < stride; i += 4)
        {
            dst[i] = lut[src[i + 2]];      // B
            dst[i + 1] = lut[src[i + 1]];  // G
            dst[i + 2] = lut[src[i]];      // R
            dst[i + 3] = 255;              // desktop captures are always opaque
        }
    }

    // ------------------------------------------------------------- statistics

    /// <summary>True when any colour channel exceeds SDR white, i.e. the frame carries real HDR.</summary>
    public static bool HasHdrPeak(Half[] rgba)
    {
        var bits = MemoryMarshal.Cast<Half, ushort>(rgba);
        for (int i = 0; i < bits.Length; i += 4)
        {
            // Alpha is skipped; sign bit set means negative, which is never HDR.
            if (IsHdrSample(bits[i]) || IsHdrSample(bits[i + 1]) || IsHdrSample(bits[i + 2]))
                return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHdrSample(ushort bits) => bits > HdrThresholdBits && bits < PositiveInfinityBits;

    /// <summary>Exposure that puts the 95th-percentile highlight at 1.0.</summary>
    private static float AcesExposure(Half[] rgba)
    {
        var samples = SampleChannelMax(rgba, 20_000);
        if (samples.Length == 0)
            return 1f;

        Array.Sort(samples);
        float p95 = samples[Math.Min(samples.Length - 1, (int)(samples.Length * 0.95))];
        return p95 > 1e-8f ? 1f / p95 : 1f;
    }

    /// <summary>Reinhard key scale from the log-average luminance.</summary>
    private static float ReinhardScale(Half[] rgba)
    {
        int pixels = rgba.Length / 4;
        int step = Math.Max(1, pixels / 50_000);
        double logSum = 0;
        int count = 0;

        for (int p = 0; p < pixels; p += step)
        {
            int i = p * 4;
            float luminance = 0.2126f * (float)rgba[i]
                            + 0.7152f * (float)rgba[i + 1]
                            + 0.0722f * (float)rgba[i + 2];
            logSum += Math.Log(Math.Max(luminance, 1e-10));
            count++;
        }

        float logAverage = (float)Math.Exp(logSum / Math.Max(count, 1));
        return 0.18f / Math.Max(logAverage, 1e-10f);
    }

    private static float[] SampleChannelMax(Half[] rgba, int budget)
    {
        int pixels = rgba.Length / 4;
        if (pixels == 0)
            return [];

        int step = Math.Max(1, pixels / budget);
        var samples = new float[(pixels + step - 1) / step];
        int n = 0;
        for (int p = 0; p < pixels && n < samples.Length; p += step)
        {
            int i = p * 4;
            samples[n++] = Math.Max((float)rgba[i], Math.Max((float)rgba[i + 1], (float)rgba[i + 2]));
        }

        return n == samples.Length ? samples : samples[..n];
    }

    // ------------------------------------------------------------- transfer

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Saturate(float v) => v <= 0f ? 0f : (v >= 1f ? 1f : v);

    private static float AcesCurve(float x)
    {
        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        return Saturate((x * (a * x + b)) / (x * (c * x + d) + e));
    }

    private static float LinearToSrgb(float linear) =>
        linear <= 0.0031308f
            ? 12.92f * linear
            : 1.055f * MathF.Pow(Math.Max(linear, 1e-10f), 1f / 2.4f) - 0.055f;
}
