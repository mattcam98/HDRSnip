using System.Diagnostics;
using System.Runtime.InteropServices;
using HDRSnip.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace HDRSnip.Capture;

/// <summary>
/// Long-lived DXGI duplication session for one monitor. The D3D device, the
/// duplication and the staging texture are all reused across snips — recreating
/// them costs hundreds of milliseconds and is the difference between a snip that
/// feels instant and one that stutters. Not thread-safe: the daemon serves one
/// request at a time.
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

    private IDXGIAdapter1? _adapter;
    private IDXGIOutput? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;

    private bool _haveDesktopImage;
    private int _stagingWidth;
    private int _stagingHeight;
    private bool _disposed;

    public DxgiOutputSession(MonitorInfo monitor) => Monitor = monitor;

    /// <summary>The output and mode (bounds, HDR state) this session was created for.</summary>
    public MonitorInfo Monitor { get; }

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

            // Updates since the last release are queued and returned at once, so a
            // warm session never needs to wait: nothing pending means nothing changed.
            uint waitMs = _haveDesktopImage ? 0u : 200u;
            var result = _duplication!.AcquireNextFrame(waitMs, out var frameInfo, out IDXGIResource? resource);
            if (result.Failure)
            {
                if (result.Code == DxgiErrorWaitTimeout)
                {
                    if (_haveDesktopImage)
                        return MakeFrame(ReadStaging());

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
                EnsureStaging(texture.Description);

                _context!.CopyResource(_staging!, texture);
                _duplication.ReleaseFrame();
                released = true;

                var pixels = ReadStaging();
                if (IsLikelyBlank(pixels))
                {
                    // A warm session that goes black has a dead duplication surface
                    // (typical after sleep without ACCESS_LOST): rebuild it. A fresh
                    // session may just be seeing DWM's black present on resume, so
                    // skip a few before giving up on it.
                    if (_haveDesktopImage)
                    {
                        ResetSession();
                        continue;
                    }

                    if (++blankPresents < MaxBlankPresents)
                        continue;
                }

                _haveDesktopImage = true;
                return MakeFrame(pixels);
            }
            finally
            {
                if (!released)
                {
                    try { _duplication?.ReleaseFrame(); } catch { /* already released */ }
                }

                resource?.Dispose();
            }
        }

        if (_haveDesktopImage)
            return MakeFrame(ReadStaging());

        throw new InvalidOperationException("Timed out waiting for a desktop frame.");
    }

    /// <summary>
    /// Copies the staging texture, which always holds the last desktop image, so
    /// it doubles as the cache for a static desktop. The source is already
    /// R16G16B16A16_FLOAT: each row is a straight memcpy with no per-sample
    /// conversion, which keeps a 4K grab in single-digit milliseconds.
    /// </summary>
    private unsafe Half[] ReadStaging()
    {
        var mapped = _context!.Map(_staging!, 0, MapMode.Read, MapFlags.None);
        try
        {
            var pixels = new Half[_stagingWidth * _stagingHeight * 4];
            int rowBytes = _stagingWidth * 4 * sizeof(ushort);
            int rowPitch = (int)mapped.RowPitch;
            byte* source = (byte*)mapped.DataPointer;

            fixed (Half* destination = pixels)
            {
                if (rowPitch == rowBytes)
                {
                    long bytes = (long)rowBytes * _stagingHeight;
                    Buffer.MemoryCopy(source, destination, bytes, bytes);
                }
                else
                {
                    byte* target = (byte*)destination;
                    for (int y = 0; y < _stagingHeight; y++)
                        Buffer.MemoryCopy(source + (long)y * rowPitch, target + (long)y * rowBytes, rowBytes, rowBytes);
                }
            }

            return pixels;
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private CapturedFrame MakeFrame(Half[] pixels) =>
        new()
        {
            Width = _stagingWidth,
            Height = _stagingHeight,
            MonitorBounds = Monitor.Bounds,
            WasHdr = Monitor.IsHdr || ToneMapper.HasHdrPeak(pixels),
            IsLinearScRgb = true,
            Rgba = pixels
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

        throw last ?? new InvalidOperationException("Failed to create a DXGI duplication session.");
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
        if (!DisplayEnumerator.TryGetOutput(factory, Monitor.OutputIndex, out _adapter!, out _output!))
            throw new InvalidOperationException($"Output {Monitor.OutputIndex} not found.");

        FeatureLevel[] featureLevels =
        [
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0
        ];

        D3D11.D3D11CreateDevice(
            _adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out _device,
            out _,
            out _context).CheckError();

        using var output5 = _output.QueryInterface<IDXGIOutput5>();
        Format[] formats = [Format.R16G16B16A16_Float];
        // Vortice's overload is (device, supportedFormatsCount, formats) — not (device, flags, formats).
        _duplication = output5.DuplicateOutput1(_device, (uint)formats.Length, formats);
    }

    private void EnsureStaging(Texture2DDescription source)
    {
        int width = (int)source.Width;
        int height = (int)source.Height;
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height)
            return;

        DisposeQuiet(ref _staging);
        _haveDesktopImage = false;
        _stagingWidth = width;
        _stagingHeight = height;
        _staging = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = source.Width,
            Height = source.Height,
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

    /// <summary>Bit pattern of the near-black threshold. Positive halves compare
    /// monotonically as integers, so masking the sign turns this into abs(v) &gt; 1e-3.</summary>
    private static readonly ushort BlankThresholdBits = BitConverter.HalfToUInt16Bits((Half)1e-3f);

    private static bool IsLikelyBlank(Half[] pixels)
    {
        var bits = MemoryMarshal.Cast<Half, ushort>(pixels);
        for (int i = 0; i < bits.Length; i += 4)
        {
            if ((bits[i] & 0x7FFF) > BlankThresholdBits ||
                (bits[i + 1] & 0x7FFF) > BlankThresholdBits ||
                (bits[i + 2] & 0x7FFF) > BlankThresholdBits)
                return false;
        }

        return true;
    }

    private static void DisposeQuiet<T>(ref T? value) where T : class, IDisposable
    {
        try { value?.Dispose(); } catch { /* teardown must not throw */ }
        value = null;
    }

    private static void TryDwmFlush()
    {
        try { _ = Native.DwmFlush(); } catch { /* best effort */ }
    }
}
