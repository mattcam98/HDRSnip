using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HDRSnip.Interop;
using HDRSnip.Models;
using HDRSnip.Services;

namespace HDRSnip.Capture;

/// <param name="Png">The image already encoded for the clipboard, when the capture flow needed it.</param>
/// <param name="SaveFailed">Auto-save was on but the file could not be written.</param>
/// <param name="Frame">
/// The float pixels behind an HDR capture, kept so the editor can tone-map it
/// again or export it as HDR. Null for SDR captures, which gain nothing from it.
/// </param>
public sealed record CaptureResult(
    BitmapSource Image, bool WasHdr, string? SavedPath, byte[]? Png = null, bool SaveFailed = false,
    CapturedFrame? Frame = null);

/// <summary>
/// Everything a capture needs, from screen grab to finished bitmap. Owns no GPU
/// resources of its own — those live in the daemon.
/// </summary>
public sealed class CaptureService(AppConfig config)
{
    /// <summary>Grabs the monitor under the cursor. Daemon first, GDI as a fallback.</summary>
    public static CapturedFrame GrabMonitorAtCursor()
    {
        var monitor = DisplayEnumerator.FindAtPoint(Native.GetCursorPosition())
                      ?? throw new InvalidOperationException("No displays found.");

        var frame = CaptureHost.Daemon?.TryCapture(monitor);
        if (frame is null)
            return GdiCapture.Capture(monitor);

        // Read here rather than in the daemon: it is a property of the desktop, not of DXGI.
        return monitor.IsHdr ? frame with { SdrWhiteNits = Native.GetSdrWhiteNits(monitor.DeviceName) } : frame;
    }

    public CaptureResult CaptureFullScreenAtCursor() => Finish(GrabMonitorAtCursor());

    /// <summary>Tone-maps a whole frame at native resolution for the selection overlay.</summary>
    public BitmapSource RenderPreview(CapturedFrame frame) => ToSdr(frame);

    public CaptureResult CropAndFinish(CapturedFrame frame, Int32Rect selection)
    {
        selection = ClampToFrame(selection, frame.Width, frame.Height);
        return Finish(frame with
        {
            Width = selection.Width,
            Height = selection.Height,
            Rgba = Crop(frame.Rgba, frame.Width, selection)
        });
    }

    /// <summary>A timestamped path in the save folder that does not exist yet.</summary>
    public string BuildSavePath()
    {
        Directory.CreateDirectory(config.SaveFolder);
        string stem = Path.Combine(config.SaveFolder, $"HDRSnip_{DateTime.Now:yyyyMMdd_HHmmss}");
        string path = stem + ".png";
        for (int n = 2; File.Exists(path); n++)
            path = $"{stem}_{n}.png";
        return path;
    }

    /// <summary>
    /// Tone-maps and encodes on the caller's (background) thread, so the UI thread
    /// only has to hand the finished bytes to the clipboard.
    /// </summary>
    private CaptureResult Finish(CapturedFrame frame)
    {
        var image = ToSdr(frame);
        byte[]? png = config.CopyToClipboard || config.AutoSave ? ImageCodec.EncodePng(image) : null;

        string? savedPath = null;
        bool saveFailed = false;
        if (config.AutoSave)
        {
            // An unwritable folder must not cost the user the capture itself.
            try
            {
                savedPath = BuildSavePath();
                File.WriteAllBytes(savedPath, png!);
            }
            catch (Exception ex)
            {
                App.LogError("AutoSave", ex);
                savedPath = null;
                saveFailed = true;
            }
        }

        return new CaptureResult(image, frame.WasHdr, savedPath, png, saveFailed,
            frame.WasHdr && frame.IsLinearScRgb ? frame : null);
    }

    public ToneMapMethod ToneMapMethod => config.ToneMapMethod;

    /// <summary>The SDR white level a frame is tone-mapped at unless the user picks another.</summary>
    public double WhiteLevelFor(CapturedFrame frame) =>
        config.AutoSdrWhite && frame.SdrWhiteNits is { } detected ? detected : config.SdrWhiteNits;

    private WriteableBitmap ToSdr(CapturedFrame frame) => ToneMap(frame, config.ToneMapMethod, WhiteLevelFor(frame));

    public static WriteableBitmap ToneMap(CapturedFrame frame, ToneMapMethod method, double whiteNits)
    {
        double dpi = Native.GetMonitorScale(
            frame.MonitorBounds.Left + frame.MonitorBounds.Width / 2,
            frame.MonitorBounds.Top + frame.MonitorBounds.Height / 2) * 96.0;

        return ToneMapper.ToSdrBitmap(frame, method, whiteNits, dpi);
    }

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
