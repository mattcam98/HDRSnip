using System.Diagnostics;
using System.IO;
using System.IO.Pipes;

namespace HDRSnip.Capture;

/// <summary>
/// Parent-side client for the persistent capture daemon.
/// Keeps DXGI crash-isolated while avoiding per-snip process spawn cost.
/// </summary>
public sealed class CaptureDaemonClient : IDisposable
{
    public const string PipeName = "HDRSnip.CaptureDaemon.v1";
    public const string DaemonArg = "--capture-daemon";

    private Process? _daemon;
    private readonly object _gate = new();
    private bool _disposed;

    public void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_daemon is { HasExited: false })
                return;

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                throw new InvalidOperationException("ProcessPath unavailable.");

            _daemon?.Dispose();
            _daemon = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { DaemonArg },
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            }) ?? throw new InvalidOperationException("Failed to start capture daemon.");

            // Wait until pipe is accept-ready.
            var sw = Stopwatch.StartNew();
            Exception? last = null;
            while (sw.Elapsed < TimeSpan.FromSeconds(12))
            {
                if (_daemon.HasExited)
                    throw new InvalidOperationException($"Capture daemon exited during startup (code {_daemon.ExitCode}).");
                try
                {
                    using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                    probe.Connect(200);
                    PipeIo.WriteLine(probe, "PING");
                    var line = PipeIo.ReadLine(probe, timeoutMs: 2000);
                    if (line == "PONG")
                        return;
                    last = new InvalidOperationException($"Unexpected ping reply: {line}");
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(40);
                }
            }

            throw new TimeoutException("Capture daemon pipe did not become ready.", last);
        }
    }

    public CapturedFrame? TryCapture(MonitorInfo monitor)
    {
        try
        {
            EnsureStarted();
            return CaptureOnce(monitor.OutputIndex, monitor.Bounds);
        }
        catch (Exception ex)
        {
            App.LogCrash("DaemonClient", ex);
            try
            {
                Restart();
                return CaptureOnce(monitor.OutputIndex, monitor.Bounds);
            }
            catch (Exception ex2)
            {
                App.LogCrash("DaemonClient.Retry", ex2);
                return null;
            }
        }
    }

    private CapturedFrame CaptureOnce(int outputIndex, System.Drawing.Rectangle bounds)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(3000);
        PipeIo.WriteLine(pipe, $"CAPTURE {outputIndex}");
        var header = PipeIo.ReadLine(pipe, timeoutMs: 20_000);

        if (header.StartsWith("ERR ", StringComparison.Ordinal))
            throw new InvalidOperationException(header[4..]);

        if (!header.StartsWith("OK ", StringComparison.Ordinal)
            || !int.TryParse(header.AsSpan(3), out int byteLen)
            || byteLen <= 0)
            throw new InvalidOperationException($"Bad daemon response: {header}");

        var payload = new byte[byteLen];
        PipeIo.ReadExact(pipe, payload, byteLen, timeoutMs: 60_000);
        return FrameSerializer.FromBytes(payload, bounds);
    }

    private void Restart()
    {
        lock (_gate)
        {
            try
            {
                if (_daemon is { HasExited: false })
                    _daemon.Kill(entireProcessTree: true);
            }
            catch { /* ignore */ }
            _daemon?.Dispose();
            _daemon = null;
        }

        EnsureStarted();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate)
        {
            try
            {
                if (_daemon is { HasExited: false })
                {
                    try
                    {
                        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                        pipe.Connect(500);
                        PipeIo.WriteLine(pipe, "QUIT");
                    }
                    catch { /* ignore */ }

                    if (!_daemon.WaitForExit(1000))
                        _daemon.Kill(entireProcessTree: true);
                }
            }
            catch { /* ignore */ }
            _daemon?.Dispose();
            _daemon = null;
        }
    }
}
