using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using HDRSnip.Models;

namespace HDRSnip.Capture;

public sealed class CaptureResult
{
    public required BitmapSource Image { get; init; }
    public required bool WasHdr { get; init; }
    public required string? SavedPath { get; init; }
}

public sealed class ScreenCaptureService
{
    private readonly AppConfig _config;
    private readonly DxgiHdrCapture _capture = new();

    public ScreenCaptureService(AppConfig config)
    {
        _config = config;
    }

    public CaptureResult CaptureFullScreenAtCursor()
    {
        GetCursorPos(out var pt);
        using var frame = _capture.CaptureMonitorAtPoint(new System.Drawing.Point(pt.X, pt.Y));
        return Finish(frame, null);
    }

    public CaptureResult CaptureFullVirtualDesktop()
    {
        using var frame = _capture.CaptureVirtualDesktop();
        return Finish(frame, null);
    }

    public (CapturedFrame Frame, BitmapSource Preview) CaptureFrozenMonitorAtCursor()
    {
        GetCursorPos(out var pt);
        var frame = _capture.CaptureMonitorAtPoint(new System.Drawing.Point(pt.X, pt.Y));
        var preview = ToneMapper.ToSdrBitmap(
            frame.RgbaLinear, frame.Width, frame.Height,
            _config.ToneMapMethod, _config.SdrWhiteNits);
        return (frame, preview);
    }

    public CaptureResult CropAndFinish(CapturedFrame frame, Int32Rect crop)
    {
        crop = NormalizeCrop(crop, frame.Width, frame.Height);
        var cropped = CropRgba(frame.RgbaLinear, frame.Width, frame.Height, crop);
        var croppedFrame = new CapturedFrame
        {
            Width = crop.Width,
            Height = crop.Height,
            MonitorBounds = frame.MonitorBounds,
            WasHdr = frame.WasHdr,
            RgbaLinear = cropped
        };
        return Finish(croppedFrame, null);
    }

    public CaptureResult Finish(CapturedFrame frame, string? forcePath)
    {
        var bmp = ToneMapper.ToSdrBitmap(
            frame.RgbaLinear, frame.Width, frame.Height,
            _config.ToneMapMethod, _config.SdrWhiteNits);

        string? saved = forcePath;
        if (_config.AutoSave || forcePath is not null)
        {
            saved ??= BuildSavePath();
            SavePng(bmp, saved);
        }

        if (_config.CopyToClipboard)
            Clipboard.SetImage(bmp);

        return new CaptureResult
        {
            Image = bmp,
            WasHdr = frame.WasHdr,
            SavedPath = saved
        };
    }

    public static void SavePng(BitmapSource image, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    public string BuildSavePath()
    {
        Directory.CreateDirectory(_config.SaveFolder);
        var name = $"HDRSnip_{DateTime.Now:yyyyMMdd_HHmmss}.png";
        return Path.Combine(_config.SaveFolder, name);
    }

    private static Int32Rect NormalizeCrop(Int32Rect crop, int width, int height)
    {
        int x = Math.Clamp(crop.X, 0, width - 1);
        int y = Math.Clamp(crop.Y, 0, height - 1);
        int w = Math.Clamp(crop.Width, 1, width - x);
        int h = Math.Clamp(crop.Height, 1, height - y);
        return new Int32Rect(x, y, w, h);
    }

    private static float[] CropRgba(float[] src, int sw, int sh, Int32Rect crop)
    {
        var dst = new float[crop.Width * crop.Height * 4];
        for (int y = 0; y < crop.Height; y++)
        {
            int srcRow = ((crop.Y + y) * sw + crop.X) * 4;
            int dstRow = y * crop.Width * 4;
            Buffer.BlockCopy(src, srcRow * sizeof(float), dst, dstRow * sizeof(float), crop.Width * 4 * sizeof(float));
        }

        return dst;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
}
