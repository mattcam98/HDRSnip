using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace HDRSnip.Capture;

/// <summary>
/// Long-lived DXGI duplication session for one monitor.
/// Reuses D3D device + DuplicateOutput1 instead of recreating every snip.
/// </summary>
public sealed class DxgiOutputSession : IDisposable
{
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);
    private const int DxgiErrorAccessDenied = unchecked((int)0x887A002B);

    private const int GrabBudgetMs = 3000;
    private const int MaxBlankPresents = 5;

    private readonly MonitorInfo _monitor;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private bool _haveDesktopImage;
    private float[]? _lastPixels;
    private int _lastWidth;
    private int _lastHeight;
    private bool _lastWasHdr;
    private bool _disposed;

    public DxgiOutputSession(MonitorInfo monitor) => _monitor = monitor;

    public bool Matches(MonitorInfo monitor) =>
        _monitor.OutputIndex == monitor.OutputIndex && _monitor.Bounds == monitor.Bounds;

    public CapturedFrame Grab()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var clock = Stopwatch.StartNew();
        int blankPresents = 0;

        while (clock.ElapsedMilliseconds < GrabBudgetMs)
        {
            try
            {
                EnsureSession();
            }
            catch
            {
                ResetSession();
                Thread.Sleep(50);
                continue;
            }

            uint waitMs = _haveDesktopImage ? 50u : 200u;
            var result = _duplication!.AcquireNextFrame(waitMs, out var frameInfo, out IDXGIResource? resource);
            if (result.Failure)
            {
                if (result.Code == DxgiErrorWaitTimeout)
                {
                    if (_haveDesktopImage && _lastPixels is not null)
                        return FrameFromCache();

                    TryDwmFlush();
                    continue;
                }

                if (IsDuplicationLost(result.Code))
                {
                    ResetSession();
                    continue;
                }

                result.CheckError();
            }

            bool released = false;
            try
            {
                bool desktopUpdated = frameInfo.LastPresentTime != 0 || frameInfo.AccumulatedFrames > 0;
                if (!desktopUpdated && !_haveDesktopImage)
                    continue;
                if (resource is null)
                    continue;

                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                var desc = texture.Description;
                int width = (int)desc.Width;
                int height = (int)desc.Height;
                EnsureStaging(desc);

                _context!.CopyResource(_staging!, texture);
                _duplication.ReleaseFrame();
                released = true;
                resource.Dispose();
                resource = null;

                var mapped = _context.Map(_staging!, 0, MapMode.Read, MapFlags.None);
                float[] pixels;
                try
                {
                    pixels = DxgiHdrCapture.ReadFp16RgbaPublic(mapped, width, height);
                }
                finally
                {
                    _context.Unmap(_staging!, 0);
                }

                if (IsLikelyBlank(pixels))
                {
                    // Warm session went black (typical after sleep without ACCESS_LOST):
                    // the duplication surface is dead — recreate and wait for a real present.
                    // Fresh session: DWM can flash a black present on resume; skip a few.
                    if (_haveDesktopImage)
                    {
                        ResetSession();
                        continue;
                    }

                    if (++blankPresents < MaxBlankPresents)
                        continue;
                }

                bool wasHdr = _monitor.IsHdr || DxgiHdrCapture.HasHdrValuesPublic(pixels);
                _haveDesktopImage = true;
                _lastPixels = pixels;
                _lastWidth = width;
                _lastHeight = height;
                _lastWasHdr = wasHdr;
                return MakeFrame(pixels, width, height, wasHdr);
            }
            finally
            {
                if (!released)
                {
                    try { _duplication?.ReleaseFrame(); } catch { /* ignore */ }
                }

                resource?.Dispose();
            }
        }

        if (_haveDesktopImage && _lastPixels is not null)
            return FrameFromCache();

        throw new InvalidOperationException("Timed out waiting for desktop frame.");
    }

    private CapturedFrame FrameFromCache() =>
        MakeFrame(_lastPixels!, _lastWidth, _lastHeight, _lastWasHdr);

    private CapturedFrame MakeFrame(float[] pixels, int width, int height, bool wasHdr) =>
        new()
        {
            Width = width,
            Height = height,
            MonitorBounds = _monitor.Bounds,
            WasHdr = wasHdr,
            IsLinearScRgb = true,
            RgbaLinear = pixels
        };

    private void EnsureSession()
    {
        if (HasLiveSession())
            return;

        ResetSession();

        Exception? last = null;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                CreateSession();
                TryDwmFlush();
                return;
            }
            catch (Exception ex)
            {
                last = ex;
                ResetSession();
                Thread.Sleep(40 * (attempt + 1));
            }
        }

        throw last ?? new InvalidOperationException("Failed to create DXGI duplication session.");
    }

    private bool HasLiveSession()
    {
        if (_duplication is null || _device is null || _context is null)
            return false;
        try
        {
            return _device.DeviceRemovedReason.Success;
        }
        catch
        {
            return false;
        }
    }

    private void CreateSession()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        if (!DxgiHdrCapture.TryGetAdapterOutputPublic(factory, _monitor.OutputIndex, out _adapter!, out _output!))
            throw new InvalidOperationException($"Output {_monitor.OutputIndex} not found.");

        var featureLevels = new[]
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        };

        D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out _,
            out _context).CheckError();

        using var output5 = _output.QueryInterface<IDXGIOutput5>();
        var formats = new[] { Format.R16G16B16A16_Float };
        // Vortice overload is (device, supportedFormatsCount, formats) — NOT (device, flags, formats).
        _duplication = output5.DuplicateOutput1(_device, (uint)formats.Length, formats);
    }

    private void EnsureStaging(Texture2DDescription srcDesc)
    {
        if (_staging is not null &&
            _staging.Description.Width == srcDesc.Width &&
            _staging.Description.Height == srcDesc.Height)
            return;

        DisposeQuiet(ref _staging);
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = srcDesc.Width,
            Height = srcDesc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R16G16B16A16_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None,
            MiscFlags = ResourceOptionFlags.None
        });
    }

    private void ResetSession()
    {
        _haveDesktopImage = false;
        _lastPixels = null;
        DisposeQuiet(ref _staging);
        DisposeQuiet(ref _duplication);
        DisposeQuiet(ref _context);
        DisposeQuiet(ref _device);
        DisposeQuiet(ref _output);
        DisposeQuiet(ref _adapter);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetSession();
    }

    private static bool IsDuplicationLost(int code) =>
        code is DxgiErrorAccessLost
            or DxgiErrorDeviceRemoved
            or DxgiErrorDeviceHung
            or DxgiErrorDeviceReset
            or DxgiErrorSessionDisconnected
            or DxgiErrorAccessDenied;

    private static bool IsLikelyBlank(float[] rgba)
    {
        for (int i = 0; i < rgba.Length; i += 4)
        {
            if (rgba[i] > 1e-3f || rgba[i + 1] > 1e-3f || rgba[i + 2] > 1e-3f)
                return false;
        }

        return true;
    }

    private static void DisposeQuiet<T>(ref T? obj) where T : class, IDisposable
    {
        try { obj?.Dispose(); } catch { /* ignore */ }
        obj = null;
    }

    private static void TryDwmFlush()
    {
        try { _ = DwmFlush(); } catch { /* ignore */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}
