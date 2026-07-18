using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;

namespace HDRSnip.Capture;

/// <summary>
/// Persistent DXGI capture process. Survives AccessViolations without taking down the tray UI.
/// Protocol over named pipe (UTF-8 lines + optional binary payload):
///   PING -> PONG
///   CAPTURE &lt;outputIndex&gt; -> OK &lt;nbytes&gt;\n + frame bytes | ERR message
///   QUIT -> exit
/// </summary>
public static class CaptureDaemon
{
    public static bool TryRun(string[] args)
    {
        if (args.Length < 1 || !args[0].Equals(CaptureDaemonClient.DaemonArg, StringComparison.OrdinalIgnoreCase))
            return false;

        // Dedicated STA for DXGI.
        Exception? fatal = null;
        var thread = new Thread(() =>
        {
            try { RunLoop(); }
            catch (Exception ex) { fatal = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = false;
        thread.Name = "HDRSnip.CaptureDaemon";
        thread.Start();
        thread.Join();

        if (fatal is not null)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDRSnip");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "errors.log"), $"[{DateTime.Now:o}] DaemonFatal: {fatal}\n");
            }
            catch { /* ignore */ }
            Environment.Exit(1);
        }

        Environment.Exit(0);
        return true;
    }

    private static void RunLoop()
    {
        var sessions = new ConcurrentDictionary<int, DxgiOutputSession>();

        while (true)
        {
            using var server = new NamedPipeServerStream(
                CaptureDaemonClient.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.None);

            server.WaitForConnection();

            string line;
            try { line = PipeIo.ReadLine(server); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in sessions.Values) s.Dispose();
                sessions.Clear();
                return;
            }

            if (line.Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                PipeIo.WriteLine(server, "PONG");
                continue;
            }

            if (line.StartsWith("CAPTURE ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(line.AsSpan(8), out int outputIndex))
            {
                try
                {
                    var monitors = DxgiHdrCapture.EnumerateMonitors();
                    var monitor = monitors.FirstOrDefault(m => m.OutputIndex == outputIndex)
                                  ?? throw new InvalidOperationException($"Output {outputIndex} not found.");

                    var session = sessions.GetOrAdd(outputIndex, _ => new DxgiOutputSession(monitor));
                    CapturedFrame frame;
                    try
                    {
                        frame = session.Grab();
                    }
                    catch
                    {
                        // Recreate session once (mode change / access lost).
                        if (sessions.TryRemove(outputIndex, out var old))
                            old.Dispose();
                        session = sessions.GetOrAdd(outputIndex, _ => new DxgiOutputSession(monitor));
                        frame = session.Grab();
                    }

                    var bytes = FrameSerializer.ToBytes(frame);
                    PipeIo.WriteLine(server, $"OK {bytes.Length}");
                    server.Write(bytes, 0, bytes.Length);
                    server.Flush();
                }
                catch (Exception ex)
                {
                    PipeIo.WriteLine(server, $"ERR {ex.Message.Replace('\r', ' ').Replace('\n', ' ')}");
                }

                continue;
            }

            PipeIo.WriteLine(server, "ERR Unknown command");
        }
    }
}
