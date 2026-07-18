using System.IO;
using System.Text;

namespace HDRSnip.Capture;

/// <summary>Line + binary framing without StreamReader (avoids stealing payload bytes).</summary>
internal static class PipeIo
{
    public static void WriteLine(Stream stream, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    public static string ReadLine(Stream stream, int timeoutMs = 30_000)
    {
        // NamedPipe streams do not support ReadTimeout (CanTimeout=false).
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            return ReadLineAsync(stream, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out reading pipe line.");
        }
    }

    public static async ValueTask<string> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream(64);
        var buf = new byte[1];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int n = await stream.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n <= 0)
                throw new EndOfStreamException("Pipe closed while reading line.");
            byte b = buf[0];
            if (b == (byte)'\n')
                break;
            if (b != (byte)'\r')
                ms.WriteByte(b);
            if (ms.Length > 4096)
                throw new InvalidOperationException("Protocol line too long.");
        }

        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public static void ReadExact(Stream stream, byte[] buffer, int count, int timeoutMs = 60_000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            ReadExactAsync(stream, buffer, count, cts.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Timed out reading pipe payload.");
        }
    }

    public static async ValueTask ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n <= 0) throw new EndOfStreamException("Incomplete pipe payload.");
            read += n;
        }
    }
}
