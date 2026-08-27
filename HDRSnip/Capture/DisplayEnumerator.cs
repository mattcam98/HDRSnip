using System.Drawing;
using Vortice.DXGI;

namespace HDRSnip.Capture;

/// <summary>Discovers DXGI outputs and reports whether each is running in HDR.</summary>
public static class DisplayEnumerator
{
    public static List<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        int outputIndex = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var desc = output.Description;
                        var coords = desc.DesktopCoordinates;

                        monitors.Add(new MonitorInfo
                        {
                            OutputIndex = outputIndex++,
                            Bounds = new Rectangle(
                                coords.Left,
                                coords.Top,
                                coords.Right - coords.Left,
                                coords.Bottom - coords.Top),
                            IsHdr = IsAdvancedColorActive(output)
                        });
                    }
                }
            }
        }

        return monitors;
    }

    public static MonitorInfo? FindAtPoint(Point screenPoint)
    {
        var monitors = Enumerate();
        return monitors.FirstOrDefault(m => m.Bounds.Contains(screenPoint)) ?? monitors.FirstOrDefault();
    }

    /// <summary>Resolves a flat output index back to its adapter/output pair. Caller owns both.</summary>
    public static bool TryGetOutput(
        IDXGIFactory1 factory,
        int targetIndex,
        out IDXGIAdapter1 adapter,
        out IDXGIOutput output)
    {
        adapter = null!;
        output = null!;

        int index = 0;
        for (uint a = 0; factory.EnumAdapters1(a, out var candidateAdapter).Success; a++)
        {
            for (uint o = 0; candidateAdapter.EnumOutputs(o, out var candidateOutput).Success; o++)
            {
                if (index == targetIndex)
                {
                    adapter = candidateAdapter;
                    output = candidateOutput;
                    return true;
                }

                candidateOutput.Dispose();
                index++;
            }

            candidateAdapter.Dispose();
        }

        return false;
    }

    private static bool IsAdvancedColorActive(IDXGIOutput output)
    {
        try
        {
            using var output6 = output.QueryInterface<IDXGIOutput6>();
            // scRGB (G10) or HDR10 (G2084) both mean the desktop is composited in HDR.
            return output6.Description1.ColorSpace
                is ColorSpaceType.RgbFullG2084NoneP2020
                or ColorSpaceType.RgbFullG10NoneP709
                or ColorSpaceType.RgbStudioG2084NoneP2020;
        }
        catch
        {
            // IDXGIOutput6 is unavailable before Windows 10 1703.
            return false;
        }
    }
}
