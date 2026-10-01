using System.Diagnostics;
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
///   WARM &lt;idx&gt;      -> OK once the output's session exists and has grabbed a frame
///                   -> ERR &lt;message&gt;
///   CAPTURE &lt;idx&gt;   -> OK &lt;map&gt; &lt;w&gt; &lt;h&gt; &lt;hdr&gt; &lt;l&gt; &lt;t&gt; &lt;bw&gt; &lt;bh&gt;, then wait for ACK
///                   -> ERR &lt;message&gt;
///   QUIT            -> exit
/// </remarks>
public static class CaptureDaemon
{
    private static Process? _owner;

    /// <summary>Runs the daemon to completion when launched as <c>--capture-daemon &lt;owner pid&gt;</c>.</summary>
    public static bool TryRun(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals(CaptureDaemonClient.DaemonArgument, StringComparison.OrdinalIgnoreCase))
            return false;

        if (args.Length < 2 || !int.TryParse(args[1], out int ownerPid))
        {
            Environment.ExitCode = 1;
            return true;
        }

        try
        {
            ExitWith(ownerPid);
            Serve(CaptureDaemonClient.PipeNameFor(ownerPid));
        }
        catch (Exception ex)
        {
            App.LogError("DaemonFatal", ex);
            Environment.ExitCode = 1;
        }

        return true;
    }

    /// <summary>A tray process that crashes or is killed never sends QUIT, so the daemon watches it instead.</summary>
    private static void ExitWith(int ownerPid)
    {
        try
        {
            _owner = Process.GetProcessById(ownerPid);
            _owner.EnableRaisingEvents = true;
            _owner.Exited += (_, _) => Environment.Exit(0);
            if (_owner.HasExited)
                Environment.Exit(0);
        }
        catch (ArgumentException)
        {
            Environment.Exit(0);
        }
    }

    private static void Serve(string pipeName)
    {
        var sessions = new Dictionary<int, DxgiOutputSession>();
        long sequence = 0;

        while (true)
        {
            using var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.None);

            server.WaitForConnection();

            string command;
            try { command = FrameChannel.ReadLine(server); }
            catch { continue; }

            if (command.Equals("QUIT", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var session in sessions.Values)
                    session.Dispose();
                return;
            }

            if (command.Equals("PING", StringComparison.OrdinalIgnoreCase))
                FrameChannel.WriteLine(server, "PONG");
            else if (TryParse(command, "CAPTURE ", out int outputIndex))
                Handle(server, sessions, outputIndex, publishAs: ++sequence);
            else if (TryParse(command, "WARM ", out outputIndex))
                Handle(server, sessions, outputIndex, publishAs: null);
            else if (!string.IsNullOrWhiteSpace(command))
                FrameChannel.WriteLine(server, "ERR Unknown command");
        }
    }

    private static bool TryParse(string command, string verb, out int outputIndex)
    {
        outputIndex = -1;
        return command.StartsWith(verb, StringComparison.OrdinalIgnoreCase)
               && int.TryParse(command.AsSpan(verb.Length), out outputIndex);
    }

    private static void Handle(
        Stream client,
        Dictionary<int, DxgiOutputSession> sessions,
        int outputIndex,
        long? publishAs)
    {
        GrabAndReply(client, sessions, outputIndex, publishAs);

        // The grab leaves a frame-sized array behind and the daemon then sits
        // idle, so nothing else would ever prompt the runtime to give it back.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Grabs one frame, then publishes it — or, when warming, only reports that it could.</summary>
    private static void GrabAndReply(
        Stream client,
        Dictionary<int, DxgiOutputSession> sessions,
        int outputIndex,
        long? publishAs)
    {
        try
        {
            var monitor = DisplayEnumerator.Enumerate().FirstOrDefault(m => m.OutputIndex == outputIndex)
                          ?? throw new InvalidOperationException($"Output {outputIndex} not found.");

            // A session is only good for the mode it was created in: a new
            // resolution, arrangement or HDR state needs a new one.
            if (!sessions.TryGetValue(outputIndex, out var session) || session.Monitor != monitor)
                session = Replace(sessions, monitor);

            CapturedFrame frame;
            try
            {
                frame = session.Grab();
            }
            catch
            {
                // Rebuild once: a device lost mid-copy is routine.
                frame = Replace(sessions, monitor).Grab();
            }

            if (publishAs is not { } sequence)
            {
                FrameChannel.WriteLine(client, "OK");
                return;
            }

            var (map, header) = FrameChannel.Publish(frame, sequence);
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

    private static DxgiOutputSession Replace(Dictionary<int, DxgiOutputSession> sessions, MonitorInfo monitor)
    {
        if (sessions.Remove(monitor.OutputIndex, out var stale))
            stale.Dispose();

        return sessions[monitor.OutputIndex] = new DxgiOutputSession(monitor);
    }
}
