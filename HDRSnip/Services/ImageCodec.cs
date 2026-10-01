using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;

namespace HDRSnip.Services;

/// <summary>Bitmap encoding for the clipboard, saved captures and the toast thumbnail.</summary>
public static class ImageCodec
{
    public static byte[] EncodePng(BitmapSource image)
    {
        using var stream = new MemoryStream();
        Encode(new PngBitmapEncoder(), image, stream);
        return stream.ToArray();
    }

    /// <summary>
    /// A copy scaled to fit <paramref name="maxEdge"/>. Materialised, so it does
    /// not keep the full-size source alive the way a lazy transform would.
    /// </summary>
    public static BitmapSource Thumbnail(BitmapSource source, int maxEdge)
    {
        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxEdge)
            return source;

        double scale = (double)maxEdge / longest;
        var thumbnail = new WriteableBitmap(new TransformedBitmap(source, new ScaleTransform(scale, scale)));
        thumbnail.Freeze();
        return thumbnail;
    }

    /// <summary>Writes the image in the format the extension names, PNG for anything unrecognised.</summary>
    public static void Save(BitmapSource image, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            ".jxr" or ".wdp" => new WmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        using var stream = File.Create(path);
        Encode(encoder, image, stream);
    }

    /// <summary>
    /// Writes an HDR capture as JPEG XR in linear scRGB (1.0 = 80 nits), the
    /// format Windows itself uses for HDR screenshots.
    /// </summary>
    /// <param name="overlay">Markup to composite on top, premultiplied BGRA the size of <paramref name="crop"/>, or null.</param>
    /// <param name="whiteNits">Brightness the markup is drawn at, so it matches SDR content on the same desktop.</param>
    public static void SaveHdr(CapturedFrame frame, Int32Rect crop, BitmapSource? overlay, double whiteNits, string path)
    {
        int width = crop.Width, height = crop.Height;
        var pixels = new float[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            int from = ((crop.Y + y) * frame.Width + crop.X) * 4;
            int to = y * width * 4;
            for (int i = 0; i < width * 4; i += 4)
            {
                pixels[to + i] = (float)frame.Rgba[from + i];
                pixels[to + i + 1] = (float)frame.Rgba[from + i + 1];
                pixels[to + i + 2] = (float)frame.Rgba[from + i + 2];
                pixels[to + i + 3] = 1f;
            }
        }

        if (overlay is not null)
            Composite(pixels, overlay, (float)(whiteNits / 80.0));

        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgba128Float, null, pixels, width * 16);
        using var stream = File.Create(path);
        Encode(new WmpBitmapEncoder { Lossless = true }, image, stream);
    }

    private static void Composite(float[] pixels, BitmapSource overlay, float white)
    {
        int stride = overlay.PixelWidth * 4;
        var marks = new byte[stride * overlay.PixelHeight];
        overlay.CopyPixels(marks, stride, 0);

        for (int i = 0; i < marks.Length; i += 4)
        {
            byte alpha = marks[i + 3];
            if (alpha == 0)
                continue;

            // Un-premultiply, decode sRGB, lift to SDR white, then blend in linear light.
            float a = alpha / 255f;
            pixels[i] = Blend(pixels[i], marks[i + 2]);
            pixels[i + 1] = Blend(pixels[i + 1], marks[i + 1]);
            pixels[i + 2] = Blend(pixels[i + 2], marks[i]);

            float Blend(float under, byte premultiplied) =>
                SrgbToLinear[Math.Min(255, (int)(premultiplied / a + 0.5f))] * white * a + under * (1 - a);
        }
    }

    private static readonly float[] SrgbToLinear = BuildSrgbToLinear();

    private static float[] BuildSrgbToLinear()
    {
        var table = new float[256];
        for (int i = 0; i < 256; i++)
        {
            float s = i / 255f;
            table[i] = s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        }

        return table;
    }

    private static void Encode(BitmapEncoder encoder, BitmapSource image, Stream stream)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }
}
