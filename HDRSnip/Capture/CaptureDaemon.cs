using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;

namespace HDRSnip.Capture;

/// <summary>
/// The capture daemon: a second instance of the executable that owns every DXGI
/// resource. Desktop duplication can raise access violations that no managed
/// handler can catch, so keeping it out of the tray process means a driver fault
/// costs one restarted child instead of the whole app.
/// </summary>
/// <remarks>
/// Protocol (UTF-8 lines over a named pipe):
///   PING            -> PONG
///   CAPTURE &lt;idx&gt;   -> OK &lt;map&gt; &lt;w&gt; &lt;h&gt; &lt;hdr&gt; &lt;l&gt; &lt;t&gt; &lt;bw&gt; &lt;bh&gt;, then wait for ACK
///                   -> ERR &lt;message&gt;
///   QUIT            -> exit
/// </remarks>
public static class CaptureDaemon
{
    /// <summary>Returns true when this process was launched as the daemon; it never returns to the caller.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length < 1 || !args[0].Equals(CaptureDaemonClient.DaemonArgument, StringComparison.OrdinalIgnoreCase))
            return false;

        // DXGI wants an STA thread of its own.
        Exception? fatal = null;
        var thread = new Thread(() =>
        {
            try { Serve(); }
            catch (Exception ex) { fatal = ex; }
        })
        {
            Name = "HDRSnip.CaptureDaemon",
            IsBackground = false
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (fatal is not null)
        {
            LogFatal(fatal);
            Environment.Exit(1);
        }

        Environment.Exit(0);
        return true;
    }

    private static void Serve()
    {
        var sessions = new ConcurrentDictionary<int, DxgiOutputSession>();
        long sequence = 0;

        while (true)
        {
            using var server = new NamedPipeServerStream(
                CaptureDaemonClient.PipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.None);

            server.WaitForConnection();

            string command;
            try { command = FrameChannel.ReadLine(server); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(command))
                continue;

            if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var session in sessions.Values)
                    session.Dispose();
                sessions.Clear();
                return;
            }

            if (command.Equals("PING", StringComparison.OrdinalIgnoreCase))
            {
                FrameChannel.WriteLine(server, "PONG");
                continue;
            }

            if (command.StartsWith("CAPTURE ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(command.AsSpan(8), out int outputIndex))
            {
                HandleCapture(server, sessions, outputIndex, ref sequence);
                continue;
            }

            FrameChannel.WriteLine(server, "ERR Unknown command");
        }
    }

    private static void HandleCapture(
        Stream client,
        ConcurrentDictionary<int, DxgiOutputSession> sessions,
        int outputIndex,
        ref long sequence)
    {
        try
        {
            var monitor = DisplayEnumerator.Enumerate().FirstOrDefault(m => m.OutputIndex == outputIndex)
                          ?? throw new InvalidOperationException($"Output {outputIndex} not found.");

            var session = GetSession(sessions, outputIndex, monitor);
            CapturedFrame frame;
            try
            {
                frame = session.Grab();
            }
            catch
            {
                // Rebuild once: a mode change or an access-lost race is routine.
                if (sessions.TryRemove(outputIndex, out var stale))
                    stale.Dispose();
                session = sessions.GetOrAdd(outputIndex, _ => new DxgiOutputSession(monitor));
                frame = session.Grab();
            }

            var (map, header) = FrameChannel.Publish(frame, ++sequence);
            using (map)
            {
                FrameChannel.WriteLine(client, header.Serialize());
                // Hold the block until the client says it has copied it out.
                try { FrameChannel.ReadLine(client, timeoutMs: 30_000); }
                catch { /* client vanished — releasing the block is the right move */ }
            }
        }
        catch (Exception ex)
        {
            var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ');
            try { FrameChannel.WriteLine(client, $"ERR {message}"); } catch { /* client gone */ }
        }
    }

    private static DxgiOutputSession GetSession(
        ConcurrentDictionary<int, DxgiOutputSession> sessions,
        int outputIndex,
        MonitorInfo monitor)
    {
        if (sessions.TryGetValue(outputIndex, out var existing) && existing.Matches(monitor))
            return existing;

        // Resolution or arrangement changed — the old duplication is worthless.
        if (sessions.TryRemove(outputIndex, out var stale))
            stale.Dispose();

        return sessions.GetOrAdd(outputIndex, _ => new DxgiOutputSession(monitor));
    }

    private static void LogFatal(Exception ex)
    {
        // The daemon has no UI and no App instance, so it writes the log itself.
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HDRSnip");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "errors.log"), $"[{DateTime.Now:o}] DaemonFatal: {ex}\n");
        }
        catch { /* nothing left to try */ }
    }
}
