using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HDRSnip.Capture;

public sealed class MonitorInfo
{
    public required int OutputIndex { get; init; }
    public required Rectangle Bounds { get; init; }
    public required string DeviceName { get; init; }
    public required bool IsHdr { get; init; }
    public required float MaxLuminance { get; init; }
}

public sealed class CapturedFrame : IDisposable
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Rectangle MonitorBounds { get; init; }
    public required bool WasHdr { get; init; }

    /// <summary>
    /// True for DXGI FP16 captures (linear scRGB, 1.0 = 80 nits).
    /// False for GDI fallback (display-referred sRGB in [0,1]).
    /// </summary>
    public required bool IsLinearScRgb { get; init; }

    /// <summary>Linear scRGB RGBA float32, length = Width*Height*4.</summary>
    public required float[] RgbaLinear { get; init; }

    public void Dispose() { }
}

/// <summary>
/// DXGI Desktop Duplication capture in R16G16B16A16_FLOAT for HDR-correct screenshots.
/// Falls back to GDI BitBlt for SDR displays when FP16 duplication is unavailable.
/// </summary>
public sealed class DxgiHdrCapture : IDisposable
{
    private bool _disposed;

    public static List<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        int outputIndex = 0;
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adapter).Success; ai++)
        {
            using (adapter)
            {
                for (uint oi = 0; adapter.EnumOutputs(oi, out var output).Success; oi++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        var bounds = new Rectangle(
                            desc.DesktopCoordinates.Left,
                            desc.DesktopCoordinates.Top,
                            desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left,
                            desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top);

                        bool isHdr = false;
                        float maxLum = 80f;
                        try
                        {
                            using var output6 = output.QueryInterface<IDXGIOutput6>();
                            var d1 = output6.Description1;
                            // scRGB (G10) or HDR10 (G2084) indicate Advanced Color / HDR desktop.
                            isHdr = d1.ColorSpace is ColorSpaceType.RgbFullG2084NoneP2020
                                or ColorSpaceType.RgbFullG10NoneP709
                                or ColorSpaceType.RgbStudioG2084NoneP2020;
                            maxLum = d1.MaxLuminance > 0 ? d1.MaxLuminance : 80f;
                        }
                        catch
                        {
                            // Output6 not available
                        }

                        list.Add(new MonitorInfo
                        {
                            OutputIndex = outputIndex,
                            Bounds = bounds,
                            DeviceName = desc.DeviceName ?? $"Display {outputIndex}",
                            IsHdr = isHdr,
                            MaxLuminance = maxLum
                        });
                        outputIndex++;
                    }
                }
            }
        }

        return list;
    }

    public CapturedFrame CaptureMonitor(MonitorInfo monitor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Prefer warm daemon (crash-isolated + session reuse). Fall back to one-shot worker, then GDI.
        var daemon = CaptureHost.Daemon;
        if (daemon is not null)
        {
            var frame = daemon.TryCapture(monitor);
            if (frame is not null)
                return frame;
        }

        var remote = CaptureWorker.TryCaptureOutOfProcess(monitor);
        if (remote is not null)
            return remote;

        return CaptureGdi(monitor);
    }

    /// <summary>In-process FP16 path used by the capture daemon / one-shot worker.</summary>
    public CapturedFrame CaptureFp16ForWorker(MonitorInfo monitor) => CaptureFp16(monitor);

    public static float[] ReadFp16RgbaPublic(MappedSubresource mapped, int width, int height) =>
        ReadFp16Rgba(mapped, width, height);

    public static bool HasHdrValuesPublic(float[] rgba) => HasHdrValues(rgba);

    public static bool TryGetAdapterOutputPublic(
        IDXGIFactory1 factory,
        int targetIndex,
        out IDXGIAdapter1 adapter,
        out IDXGIOutput output) =>
        TryGetAdapterOutput(factory, targetIndex, out adapter, out output);

    public CapturedFrame CaptureMonitorAtPoint(Point screenPoint)
    {
        var monitors = EnumerateMonitors();
        var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(screenPoint))
                      ?? monitors.FirstOrDefault()
                      ?? throw new InvalidOperationException("No monitors found.");
        return CaptureMonitor(monitor);
    }

    public CapturedFrame CaptureVirtualDesktop()
    {
        var monitors = EnumerateMonitors();
        if (monitors.Count == 0)
            throw new InvalidOperationException("No monitors found.");

        if (monitors.Count == 1)
            return CaptureMonitor(monitors[0]);

        int left = monitors.Min(m => m.Bounds.Left);
        int top = monitors.Min(m => m.Bounds.Top);
        int right = monitors.Max(m => m.Bounds.Right);
        int bottom = monitors.Max(m => m.Bounds.Bottom);
        int vw = right - left;
        int vh = bottom - top;

        var composed = new float[vw * vh * 4];
        bool anyHdr = false;
        bool allLinear = true;

        foreach (var m in monitors)
        {
            using var frame = CaptureMonitor(m);
            anyHdr |= frame.WasHdr;
            allLinear &= frame.IsLinearScRgb;
            int ox = m.Bounds.Left - left;
            int oy = m.Bounds.Top - top;
            BlitRgba(frame.RgbaLinear, frame.Width, frame.Height, composed, vw, vh, ox, oy);
        }

        return new CapturedFrame
        {
            Width = vw,
            Height = vh,
            MonitorBounds = new Rectangle(left, top, vw, vh),
            WasHdr = anyHdr,
            IsLinearScRgb = allLinear,
            RgbaLinear = composed
        };
    }

    private static CapturedFrame CaptureFp16(MonitorInfo monitor)
    {
        using var session = new DxgiOutputSession(monitor);
        return session.Grab();
    }

    private static unsafe float[] ReadFp16Rgba(MappedSubresource mapped, int width, int height)
    {
        var result = new float[width * height * 4];
        byte* basePtr = (byte*)mapped.DataPointer;
        int rowPitch = (int)mapped.RowPitch;

        fixed (float* dst = result)
        {
            for (int y = 0; y < height; y++)
            {
                var srcRow = (Half*)(basePtr + y * rowPitch);
                float* dstRow = dst + y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int i = x * 4;
                    dstRow[i] = (float)srcRow[i];
                    dstRow[i + 1] = (float)srcRow[i + 1];
                    dstRow[i + 2] = (float)srcRow[i + 2];
                    dstRow[i + 3] = (float)srcRow[i + 3];
                }
            }
        }

        return result;
    }

    private static bool HasHdrValues(float[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 1.02f || rgba[i + 1] > 1.02f || rgba[i + 2] > 1.02f)
                return true;
        }
        return false;
    }

    private static CapturedFrame CaptureGdi(MonitorInfo monitor)
    {
        var bounds = monitor.Bounds;
        using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int w = bmp.Width, h = bmp.Height;
            var rgba = new float[w * h * 4];
            unsafe
            {
                byte* ptr = (byte*)data.Scan0;
                for (int y = 0; y < h; y++)
                {
                    byte* row = ptr + y * data.Stride;
                    for (int x = 0; x < w; x++)
                    {
                        int o = (y * w + x) * 4;
                        // Display-referred SDR in [0,1] — ToneMapper will pass through.
                        rgba[o] = row[x * 4 + 2] / 255f;
                        rgba[o + 1] = row[x * 4 + 1] / 255f;
                        rgba[o + 2] = row[x * 4] / 255f;
                        rgba[o + 3] = 1f;
                    }
                }
            }

            return new CapturedFrame
            {
                Width = w,
                Height = h,
                MonitorBounds = bounds,
                WasHdr = false,
                IsLinearScRgb = false,
                RgbaLinear = rgba
            };
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static bool TryGetAdapterOutput(
        IDXGIFactory1 factory,
        int targetIndex,
        out IDXGIAdapter1 adapter,
        out IDXGIOutput output)
    {
        adapter = null!;
        output = null!;
        int idx = 0;
        for (uint ai = 0; factory.EnumAdapters1(ai, out var adp).Success; ai++)
        {
            for (uint oi = 0; adp.EnumOutputs(oi, out var outp).Success; oi++)
            {
                if (idx == targetIndex)
                {
                    adapter = adp;
                    output = outp;
                    return true;
                }

                outp.Dispose();
                idx++;
            }

            adp.Dispose();
        }

        return false;
    }

    private static void BlitRgba(
        float[] src, int sw, int sh,
        float[] dst, int dw, int dh,
        int ox, int oy)
    {
        for (int y = 0; y < sh; y++)
        {
            int dy = oy + y;
            if (dy < 0 || dy >= dh) continue;
            for (int x = 0; x < sw; x++)
            {
                int dx = ox + x;
                if (dx < 0 || dx >= dw) continue;
                int si = (y * sw + x) * 4;
                int di = (dy * dw + dx) * 4;
                dst[di] = src[si];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
    }

    public void Dispose() => _disposed = true;
}
