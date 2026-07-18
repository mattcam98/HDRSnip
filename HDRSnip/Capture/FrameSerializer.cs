using System.IO;
using System.Runtime.InteropServices;

namespace HDRSnip.Capture;

public static class FrameSerializer
{
    public static byte[] ToBytes(CapturedFrame frame)
    {
        using var ms = new MemoryStream(32 + frame.RgbaLinear.Length * sizeof(float));
        using var bw = new BinaryWriter(ms);
        bw.Write(frame.Width);
        bw.Write(frame.Height);
        bw.Write(frame.WasHdr);
        bw.Write(frame.MonitorBounds.Left);
        bw.Write(frame.MonitorBounds.Top);
        bw.Write(frame.MonitorBounds.Width);
        bw.Write(frame.MonitorBounds.Height);
        var rgbaBytes = MemoryMarshal.AsBytes(frame.RgbaLinear.AsSpan());
        bw.Write(rgbaBytes.Length);
        bw.Write(rgbaBytes);
        return ms.ToArray();
    }

    public static CapturedFrame FromBytes(byte[] payload, System.Drawing.Rectangle fallbackBounds)
    {
        using var ms = new MemoryStream(payload);
        using var br = new BinaryReader(ms);
        int width = br.ReadInt32();
        int height = br.ReadInt32();
        bool wasHdr = br.ReadBoolean();
        int left = br.ReadInt32();
        int top = br.ReadInt32();
        int bw = br.ReadInt32();
        int bh = br.ReadInt32();
        int byteLen = br.ReadInt32();
        var bytes = br.ReadBytes(byteLen);
        var rgba = new float[width * height * 4];
        MemoryMarshal.Cast<byte, float>(bytes).CopyTo(rgba);

        return new CapturedFrame
        {
            Width = width,
            Height = height,
            WasHdr = wasHdr,
            MonitorBounds = bw > 0 && bh > 0
                ? new System.Drawing.Rectangle(left, top, bw, bh)
                : fallbackBounds,
            RgbaLinear = rgba
        };
    }
}
