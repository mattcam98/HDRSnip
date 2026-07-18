using System.Drawing;
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
    private readonly MonitorInfo _monitor;
    private IDXGIAdapter1? _adapter;
    private IDXGIOutput? _output;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _staging;
    private bool _disposed;

    public DxgiOutputSession(MonitorInfo monitor) => _monitor = monitor;

    public CapturedFrame Grab()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureSession();

        float[]? pixels = null;
        bool wasHdr = _monitor.IsHdr;
        int width = _monitor.Bounds.Width;
        int height = _monitor.Bounds.Height;

        for (int attempt = 0; attempt < 25; attempt++)
        {
            var result = _duplication!.AcquireNextFrame(40, out var frameInfo, out IDXGIResource? resource);
            if (result.Failure)
            {
                if (result.Code == unchecked((int)0x887A0027)) // WAIT_TIMEOUT
                    continue;
                if (result.Code is unchecked((int)0x887A0026) or unchecked((int)0x887A0006)) // ACCESS_LOST / DENIED
                {
                    ResetSession();
                    EnsureSession();
                    continue;
                }
                result.CheckError();
            }

            bool released = false;
            try
            {
                if (frameInfo.LastPresentTime == 0 && attempt < 4)
                    continue;
                if (resource is null)
                    continue;

                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                var desc = texture.Description;
                width = (int)desc.Width;
                height = (int)desc.Height;
                EnsureStaging(desc);

                _context!.CopyResource(_staging!, texture);
                _duplication.ReleaseFrame();
                released = true;
                resource.Dispose();
                resource = null;

                var mapped = _context.Map(_staging!, 0, MapMode.Read, MapFlags.None);
                try
                {
                    pixels = DxgiHdrCapture.ReadFp16RgbaPublic(mapped, width, height);
                    wasHdr = wasHdr || DxgiHdrCapture.HasHdrValuesPublic(pixels);
                }
                finally
                {
                    _context.Unmap(_staging!, 0);
                }

                break;
            }
            finally
            {
                if (!released)
                {
                    try { _duplication.ReleaseFrame(); } catch { /* ignore */ }
                }
                resource?.Dispose();
            }
        }

        if (pixels is null)
            throw new InvalidOperationException("Timed out waiting for desktop frame.");

        return new CapturedFrame
        {
            Width = width,
            Height = height,
            MonitorBounds = _monitor.Bounds,
            WasHdr = wasHdr,
            RgbaLinear = pixels
        };
    }

    private void EnsureSession()
    {
        if (_duplication is not null && _device is not null && _context is not null)
            return;

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
        _duplication = output5.DuplicateOutput1(_device, (uint)formats.Length, formats);
    }

    private void EnsureStaging(Texture2DDescription srcDesc)
    {
        if (_staging is not null &&
            _staging.Description.Width == srcDesc.Width &&
            _staging.Description.Height == srcDesc.Height)
            return;

        _staging?.Dispose();
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
        _staging?.Dispose();
        _staging = null;
        _duplication?.Dispose();
        _duplication = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _output?.Dispose();
        _output = null;
        _adapter?.Dispose();
        _adapter = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetSession();
    }
}
