using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Models;

namespace HDRSnip.Capture;

/// <summary>
/// HDR → SDR tone mapping for scRGB linear frames (1.0 = 80 nits).
/// Windows/OBS path: divide by (sdrWhiteNits/80), clip, sRGB encode.
/// </summary>
public static class ToneMapper
{
    public static WriteableBitmap ToSdrBitmap(
        float[] rgbaLinear,
        int width,
        int height,
        ToneMapMethod method,
        double sdrWhiteNits)
    {
        var pixels = new byte[width * height * 4];
        MapToBgra8(rgbaLinear, pixels, width, height, method, sdrWhiteNits);

        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    public static void MapToBgra8(
        float[] rgbaLinear,
        byte[] bgra8,
        int width,
        int height,
        ToneMapMethod method,
        double sdrWhiteNits)
    {
        int count = width * height;
        bool isHdr = false;
        for (int i = 0; i < count * 4; i += 4)
        {
            if (rgbaLinear[i] > 1.02f || rgbaLinear[i + 1] > 1.02f || rgbaLinear[i + 2] > 1.02f)
            {
                isHdr = true;
                break;
            }
        }

        if (!isHdr)
        {
            // Already display-referred SDR in [0,1] — treat as linear-ish passthrough with clamp.
            for (int i = 0, o = 0; i < count; i++, o += 4)
            {
                int src = i * 4;
                bgra8[o] = ToByte(rgbaLinear[src + 2]);     // B
                bgra8[o + 1] = ToByte(rgbaLinear[src + 1]); // G
                bgra8[o + 2] = ToByte(rgbaLinear[src]);     // R
                bgra8[o + 3] = 255;
            }
            return;
        }

        switch (method)
        {
            case ToneMapMethod.Aces:
                MapAces(rgbaLinear, bgra8, count);
                break;
            case ToneMapMethod.Reinhard:
                MapReinhard(rgbaLinear, bgra8, count);
                break;
            default:
                MapWindows(rgbaLinear, bgra8, count, sdrWhiteNits);
                break;
        }
    }

    private static void MapWindows(float[] src, byte[] dst, int count, double sdrWhiteNits)
    {
        float scale = (float)(sdrWhiteNits / 80.0);
        if (scale < 0.01f) scale = 0.01f;

        for (int i = 0, o = 0; i < count; i++, o += 4)
        {
            int s = i * 4;
            float r = Math.Clamp(src[s] / scale, 0f, 1f);
            float g = Math.Clamp(src[s + 1] / scale, 0f, 1f);
            float b = Math.Clamp(src[s + 2] / scale, 0f, 1f);
            dst[o] = ToByte(LinearToSrgb(b));
            dst[o + 1] = ToByte(LinearToSrgb(g));
            dst[o + 2] = ToByte(LinearToSrgb(r));
            dst[o + 3] = 255;
        }
    }

    private static void MapAces(float[] src, byte[] dst, int count)
    {
        // Auto-expose to ~95th percentile peak (sampled).
        float peak = 0;
        int step = Math.Max(1, count / 20000);
        var samples = new List<float>(20000);
        for (int i = 0; i < count; i += step)
        {
            int s = i * 4;
            float m = Math.Max(src[s], Math.Max(src[s + 1], src[s + 2]));
            samples.Add(m);
            if (m > peak) peak = m;
        }

        samples.Sort();
        float p95 = samples[(int)(samples.Count * 0.95)];
        float exposure = p95 > 1e-8f ? 1f / p95 : 1f;

        const float a = 2.51f, b = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
        for (int i = 0, o = 0; i < count; i++, o += 4)
        {
            int s = i * 4;
            float r = AcesCurve(src[s] * exposure, a, b, c, d, e);
            float g = AcesCurve(src[s + 1] * exposure, a, b, c, d, e);
            float bl = AcesCurve(src[s + 2] * exposure, a, b, c, d, e);
            dst[o] = ToByte(LinearToSrgb(bl));
            dst[o + 1] = ToByte(LinearToSrgb(g));
            dst[o + 2] = ToByte(LinearToSrgb(r));
            dst[o + 3] = 255;
        }
    }

    private static float AcesCurve(float x, float a, float b, float c, float d, float e) =>
        Math.Clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0f, 1f);

    private static void MapReinhard(float[] src, byte[] dst, int count)
    {
        double logSum = 0;
        int step = Math.Max(1, count / 50000);
        int n = 0;
        for (int i = 0; i < count; i += step)
        {
            int s = i * 4;
            float lum = 0.2126f * src[s] + 0.7152f * src[s + 1] + 0.0722f * src[s + 2];
            logSum += Math.Log(Math.Max(lum, 1e-10));
            n++;
        }

        float logAvg = (float)Math.Exp(logSum / Math.Max(n, 1));
        float scale = 0.18f / Math.Max(logAvg, 1e-10f);

        for (int i = 0, o = 0; i < count; i++, o += 4)
        {
            int s = i * 4;
            float r = src[s] * scale;
            float g = src[s + 1] * scale;
            float b = src[s + 2] * scale;
            r = Math.Clamp(r / (1f + r), 0f, 1f);
            g = Math.Clamp(g / (1f + g), 0f, 1f);
            b = Math.Clamp(b / (1f + b), 0f, 1f);
            dst[o] = ToByte(LinearToSrgb(b));
            dst[o + 1] = ToByte(LinearToSrgb(g));
            dst[o + 2] = ToByte(LinearToSrgb(r));
            dst[o + 3] = 255;
        }
    }

    private static float LinearToSrgb(float linear)
    {
        if (linear <= 0.0031308f)
            return 12.92f * linear;
        return 1.055f * MathF.Pow(Math.Max(linear, 1e-10f), 1f / 2.4f) - 0.055f;
    }

    private static byte ToByte(float v) =>
        (byte)Math.Clamp((int)(v * 255f + 0.5f), 0, 255);
}
