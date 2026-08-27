using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HDRSnip.Interop;
using HDRSnip.Models;

namespace HDRSnip.Capture;

public sealed record CaptureResult(BitmapSource Image, bool WasHdr, string? SavedPath);

/// <summary>
/// Everything a capture needs, from screen grab to finished bitmap. Owns no GPU
/// resources of its own — those live in the daemon.
/// </summary>
public sealed class CaptureService(AppConfig config)
{
    private readonly AppConfig _config = config;

    // ------------------------------------------------------------ acquisition

    /// <summary>Grabs the monitor under the cursor. Daemon first, GDI as a fallback.</summary>
    public CapturedFrame GrabMonitorAtCursor()
    {
        var monitor = DisplayEnumerator.FindAtPoint(Native.GetCursorPosition())
                      ?? throw new InvalidOperationException("No displays found.");

        var frame = CaptureHost.Daemon?.TryCapture(monitor);
        return frame ?? GdiCapture.Capture(monitor);
    }

    public CaptureResult CaptureFullScreenAtCursor() => Finish(GrabMonitorAtCursor());

    /// <summary>
    /// Tone-maps a frame at native resolution for the selection overlay. The
    /// overlay covers the whole monitor, so anything downscaled here would be
    /// stretched back up and look soft under the selection rectangle.
    /// </summary>
    public BitmapSource RenderPreview(CapturedFrame frame) => ToSdr(frame);

    public CaptureResult CropAndFinish(CapturedFrame frame, Int32Rect selection)
    {
        selection = ClampToFrame(selection, frame.Width, frame.Height);
        return Finish(new CapturedFrame
        {
            Width = selection.Width,
            Height = selection.Height,
            MonitorBounds = frame.MonitorBounds,
            WasHdr = frame.WasHdr,
            IsLinearScRgb = frame.IsLinearScRgb,
            Rgba = Crop(frame.Rgba, frame.Width, selection)
        });
    }

    // ------------------------------------------------------------ finishing

    private CaptureResult Finish(CapturedFrame frame)
    {
        var image = ToSdr(frame);

        string? savedPath = null;
        if (_config.AutoSave)
        {
            savedPath = BuildSavePath();
            SavePng(image, savedPath);
        }

        return new CaptureResult(image, frame.WasHdr, savedPath);
    }

    private BitmapSource ToSdr(CapturedFrame frame)
    {
        double dpi = Native.GetMonitorScale(
            frame.MonitorBounds.Left + frame.MonitorBounds.Width / 2,
            frame.MonitorBounds.Top + frame.MonitorBounds.Height / 2) * 96.0;

        return ToneMapper.ToSdrBitmap(frame, _config.ToneMapMethod, _config.SdrWhiteNits, dpi);
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public string BuildSavePath()
    {
        Directory.CreateDirectory(_config.SaveFolder);
        return Path.Combine(_config.SaveFolder, $"HDRSnip_{DateTime.Now:yyyyMMdd_HHmmss}.png");
    }

    // ------------------------------------------------------------ geometry

    private static Int32Rect ClampToFrame(Int32Rect rect, int width, int height)
    {
        int x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        int y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        return new Int32Rect(
            x, y,
            Math.Clamp(rect.Width, 1, width - x),
            Math.Clamp(rect.Height, 1, height - y));
    }

    private static Half[] Crop(Half[] source, int sourceWidth, Int32Rect crop)
    {
        var cropped = new Half[crop.Width * crop.Height * 4];
        int rowSamples = crop.Width * 4;

        for (int y = 0; y < crop.Height; y++)
        {
            int from = ((crop.Y + y) * sourceWidth + crop.X) * 4;
            Array.Copy(source, from, cropped, y * rowSamples, rowSamples);
        }

        return cropped;
    }
}
