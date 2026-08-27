using System.Drawing;
using System.Drawing.Imaging;

namespace HDRSnip.Capture;

/// <summary>
/// Last-resort BitBlt capture, used when DXGI duplication is unavailable —
/// remote sessions, some virtual machines, locked-down GPU drivers. Output is
/// display-referred sRGB, so the tone mapper passes it through unchanged.
/// </summary>
public static class GdiCapture
{
    /// <summary>8-bit source, so 256 possible samples: convert once, look up thereafter.</summary>
    private static readonly Half[] ByteToHalf = BuildByteTable();

    private static Half[] BuildByteTable()
    {
        var table = new Half[256];
        for (int i = 0; i < 256; i++)
            table[i] = (Half)(i / 255f);
        return table;
    }

    public static CapturedFrame Capture(MonitorInfo monitor)
    {
        var bounds = monitor.Bounds;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int width = bitmap.Width;
            int height = bitmap.Height;
            var pixels = new Half[width * height * 4];

            unsafe
            {
                byte* scan0 = (byte*)data.Scan0;
                int stride = data.Stride;
                fixed (Half* table = ByteToHalf)
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* row = scan0 + (long)y * stride;
                        int offset = y * width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int src = x * 4;
                            int dst = offset + src;
                            pixels[dst] = table[row[src + 2]];      // R
                            pixels[dst + 1] = table[row[src + 1]];  // G
                            pixels[dst + 2] = table[row[src]];      // B
                            pixels[dst + 3] = table[255];           // A
                        }
                    }
                }
            }

            return new CapturedFrame
            {
                Width = width,
                Height = height,
                MonitorBounds = bounds,
                WasHdr = false,
                IsLinearScRgb = false,
                Rgba = pixels
            };
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
