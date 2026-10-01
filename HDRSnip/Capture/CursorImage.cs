using System.Drawing;
using System.Drawing.Imaging;
using HDRSnip.Interop;

namespace HDRSnip.Capture;

/// <summary>
/// The mouse pointer as it looked at capture time, positioned in frame pixels.
/// Desktop duplication delivers the desktop without the pointer, so it is read
/// separately and drawn on after tone mapping.
/// </summary>
/// <param name="Pixels">
/// Premultiplied BGRA. A pixel with zero alpha and non-zero colour is an
/// "invert" pixel, as used by the text I-beam: it flips whatever is beneath it.
/// </param>
public sealed record CursorImage(int X, int Y, int Width, int Height, byte[] Pixels)
{
    public CursorImage Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Reads the pointer currently on screen, or null if it is hidden.</summary>
    public static CursorImage? Capture(Rectangle monitorBounds)
    {
        if (Native.GetVisibleCursor() is not { } cursor)
            return null;

        try
        {
            using var icon = Icon.FromHandle(cursor.Handle);
            int width = icon.Width, height = icon.Height;

            // Drawn over black and over white: the difference between the two
            // recovers alpha, and a pixel brighter on black than on white inverts.
            var onBlack = Render(icon, Color.Black);
            var onWhite = Render(icon, Color.White);
            var pixels = new byte[width * height * 4];

            for (int i = 0; i < pixels.Length; i += 4)
            {
                int alpha = 255 - (onWhite[i + 1] - onBlack[i + 1]);
                if (alpha > 255)
                {
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                }
                else
                {
                    pixels[i] = onBlack[i];
                    pixels[i + 1] = onBlack[i + 1];
                    pixels[i + 2] = onBlack[i + 2];
                    pixels[i + 3] = (byte)Math.Max(alpha, 0);
                }
            }

            return new CursorImage(
                cursor.Position.X - cursor.Hotspot.X - monitorBounds.Left,
                cursor.Position.Y - cursor.Hotspot.Y - monitorBounds.Top,
                width, height, pixels);
        }
        catch (Exception ex)
        {
            App.LogError("Cursor", ex);
            return null;
        }
    }

    /// <summary>Composites the pointer onto opaque BGRA pixels, clipped to their bounds.</summary>
    public void DrawOnto(byte[] target, int targetWidth, int targetHeight)
    {
        for (int y = Math.Max(0, -Y); y < Height && Y + y < targetHeight; y++)
        {
            for (int x = Math.Max(0, -X); x < Width && X + x < targetWidth; x++)
            {
                int from = (y * Width + x) * 4;
                int to = ((Y + y) * targetWidth + X + x) * 4;
                byte alpha = Pixels[from + 3];

                if (alpha == 0)
                {
                    if (Pixels[from] != 0)
                    {
                        target[to] = (byte)(255 - target[to]);
                        target[to + 1] = (byte)(255 - target[to + 1]);
                        target[to + 2] = (byte)(255 - target[to + 2]);
                    }

                    continue;
                }

                for (int c = 0; c < 3; c++)
                    target[to + c] = (byte)Math.Min(255, Pixels[from + c] + target[to + c] * (255 - alpha) / 255);
            }
        }
    }

    private static byte[] Render(Icon icon, Color background)
    {
        using var bitmap = new Bitmap(icon.Width, icon.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(background);
            graphics.DrawIcon(icon, 0, 0);
        }

        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[bitmap.Width * bitmap.Height * 4];
            for (int y = 0; y < bitmap.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
            return pixels;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
