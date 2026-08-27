using System.Drawing;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;

namespace HDRSnip.Capture;

/// <summary>
/// Transport between the tray app and the capture daemon.
/// </summary>
/// <remarks>
/// Commands are UTF-8 lines over a named pipe, but pixels never travel through
/// it. A 4K half-float frame is 66 MB; streaming that down a pipe costs a copy
/// on each side plus the kernel round trip. Instead the daemon publishes the
/// frame into shared memory and sends only its name, so the client maps the same
/// physical pages and performs exactly one copy.
/// </remarks>
internal static class FrameChannel
{
    private const int MaxLineLength = 4096;

    // ------------------------------------------------------------ line framing

    public static void WriteLine(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    /// <summary>
    /// Reads one line. Deliberately avoids <see cref="StreamReader"/>, which
    /// buffers ahead and would swallow bytes belonging to the next message.
    /// </summary>
    public static string ReadLine(Stream stream, int timeoutMs = 30_000)
    {
        // Named pipe streams report CanTimeout = false, so cancellation does the work.
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return ReadLineAsync(stream, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out reading from the capture pipe.");
        }
    }

    private static async ValueTask<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream(64);
        var one = new byte[1];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (read <= 0)
                throw new EndOfStreamException("Capture pipe closed mid-line.");

            byte b = one[0];
            if (b == (byte)'\n')
                break;
            if (b != (byte)'\r')
                buffer.WriteByte(b);
            if (buffer.Length > MaxLineLength)
                throw new InvalidOperationException("Capture protocol line too long.");
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    // ------------------------------------------------------------ shared frames

    /// <summary>Header describing a frame parked in shared memory.</summary>
    public readonly record struct FrameHeader(
        string MapName,
        int Width,
        int Height,
        bool WasHdr,
        Rectangle Bounds)
    {
        public long ByteCount => (long)Width * Height * 4 * sizeof(ushort);

        public string Serialize() =>
            $"OK {MapName} {Width} {Height} {(WasHdr ? 1 : 0)} " +
            $"{Bounds.Left} {Bounds.Top} {Bounds.Width} {Bounds.Height}";

        public static bool TryParse(string line, out FrameHeader header)
        {
            header = default;
            var parts = line.Split(' ');
            if (parts.Length != 9 || parts[0] != "OK")
                return false;

            if (!int.TryParse(parts[2], out int width) ||
                !int.TryParse(parts[3], out int height) ||
                !int.TryParse(parts[4], out int hdr) ||
                !int.TryParse(parts[5], out int left) ||
                !int.TryParse(parts[6], out int top) ||
                !int.TryParse(parts[7], out int boundsWidth) ||
                !int.TryParse(parts[8], out int boundsHeight))
                return false;

            if (width <= 0 || height <= 0)
                return false;

            header = new FrameHeader(parts[1], width, height, hdr != 0,
                new Rectangle(left, top, boundsWidth, boundsHeight));
            return true;
        }
    }

    /// <summary>
    /// Copies a frame into a uniquely named shared block. The returned handle
    /// must stay alive until the client confirms it has read the block.
    /// </summary>
    public static unsafe (MemoryMappedFile Map, FrameHeader Header) Publish(CapturedFrame frame, long sequence)
    {
        string name = $"HDRSnip.Frame.{Environment.ProcessId}.{sequence}";
        long byteCount = (long)frame.Rgba.Length * sizeof(ushort);

        var map = MemoryMappedFile.CreateNew(name, byteCount, MemoryMappedFileAccess.ReadWrite);
        try
        {
            using var view = map.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Write);
            byte* target = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref target);
            try
            {
                fixed (Half* source = frame.Rgba)
                    Buffer.MemoryCopy(source, target, byteCount, byteCount);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
        catch
        {
            map.Dispose();
            throw;
        }

        var header = new FrameHeader(name, frame.Width, frame.Height, frame.WasHdr, frame.MonitorBounds);
        return (map, header);
    }

    /// <summary>Maps a published block and copies it into a managed frame.</summary>
    public static unsafe CapturedFrame Consume(in FrameHeader header, Rectangle fallbackBounds)
    {
        long byteCount = header.ByteCount;
        var pixels = new Half[header.Width * header.Height * 4];

        using (var map = MemoryMappedFile.OpenExisting(header.MapName, MemoryMappedFileRights.Read))
        using (var view = map.CreateViewAccessor(0, byteCount, MemoryMappedFileAccess.Read))
        {
            byte* source = null;
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref source);
            try
            {
                fixed (Half* target = pixels)
                    Buffer.MemoryCopy(source, target, byteCount, byteCount);
            }
            finally
            {
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }

        return new CapturedFrame
        {
            Width = header.Width,
            Height = header.Height,
            WasHdr = header.WasHdr,
            // The daemon only ever serves DXGI FP16 frames.
            IsLinearScRgb = true,
            MonitorBounds = header.Bounds is { Width: > 0, Height: > 0 } ? header.Bounds : fallbackBounds,
            Rgba = pixels
        };
    }
}
