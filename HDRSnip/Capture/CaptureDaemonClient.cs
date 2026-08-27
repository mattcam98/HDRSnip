using System.Diagnostics;
using System.IO.Pipes;

namespace HDRSnip.Capture;

/// <summary>
/// Tray-side client for the capture daemon. Starts it lazily, keeps it warm,
/// restarts it once after a fault, and gives up permanently if the machine
/// simply cannot run DXGI duplication — so a virtual machine never pays a
/// process-launch timeout on every single snip.
/// </summary>
public sealed class CaptureDaemonClient : IDisposable
{
    public const string PipeName = "HDRSnip.CaptureDaemon.v2";
    public const string DaemonArgument = "--capture-daemon";

    private const int StartupBudgetMs = 6000;
    private const int MaxConsecutiveFailures = 2;

    private readonly object _gate = new();
    private Process? _daemon;
    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>False once the daemon has failed enough times to be written off for this session.</summary>
    public bool IsUsable => _consecutiveFailures < MaxConsecutiveFailures;

    public void EnsureStarted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_daemon is { HasExited: false })
                return;

            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Process path unavailable.");

            _daemon?.Dispose();
            _daemon = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                ArgumentList = { DaemonArgument },
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            }) ?? throw new InvalidOperationException("Failed to start the capture daemon.");

            WaitUntilReady();
        }
    }

    public CapturedFrame? TryCapture(MonitorInfo monitor)
    {
        if (!IsUsable)
            return null;

        try
        {
            EnsureStarted();
            var frame = CaptureOnce(monitor);
            _consecutiveFailures = 0;
            return frame;
        }
        catch (Exception ex)
        {
            App.LogError("CaptureDaemon", ex);
        }

        try
        {
            Restart();
            var frame = CaptureOnce(monitor);
            _consecutiveFailures = 0;
            return frame;
        }
        catch (Exception ex)
        {
            App.LogError("CaptureDaemon.Retry", ex);
            _consecutiveFailures++;
            return null;
        }
    }

    private void WaitUntilReady()
    {
        var clock = Stopwatch.StartNew();
        Exception? last = null;

        while (clock.ElapsedMilliseconds < StartupBudgetMs)
        {
            if (_daemon!.HasExited)
                throw new InvalidOperationException($"Capture daemon exited during startup (code {_daemon.ExitCode}).");

            try
            {
                using var probe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
                probe.Connect(200);
                FrameChannel.WriteLine(probe, "PING");
                if (FrameChannel.ReadLine(probe, timeoutMs: 2000) == "PONG")
                    return;

                last = new InvalidOperationException("Capture daemon returned an unexpected ping reply.");
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(40);
            }
        }

        throw new TimeoutException("Capture daemon pipe never became ready.", last);
    }

    private CapturedFrame CaptureOnce(MonitorInfo monitor)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(3000);
        FrameChannel.WriteLine(pipe, $"CAPTURE {monitor.OutputIndex}");

        var response = FrameChannel.ReadLine(pipe, timeoutMs: 20_000);
        if (response.StartsWith("ERR ", StringComparison.Ordinal))
            throw new InvalidOperationException(response[4..]);

        if (!FrameChannel.FrameHeader.TryParse(response, out var header))
            throw new InvalidOperationException($"Malformed daemon response: {response}");

        try
        {
            return FrameChannel.Consume(header, monitor.Bounds);
        }
        finally
        {
            // Releases the daemon's hold on the shared block.
            try { FrameChannel.WriteLine(pipe, "ACK"); } catch { /* daemon gone */ }
        }
    }

    private void Restart()
    {
        lock (_gate)
        {
            KillDaemon();
        }

        EnsureStarted();
    }

    private void KillDaemon()
    {
        try
        {
            if (_daemon is { HasExited: false })
                _daemon.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }

        _daemon?.Dispose();
        _daemon = null;
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
                        FrameChannel.WriteLine(pipe, "QUIT");
                    }
                    catch { /* fall through to Kill */ }

                    if (!_daemon.WaitForExit(1000))
                        _daemon.Kill(entireProcessTree: true);
                }
            }
            catch { /* shutdown must not throw */ }

            _daemon?.Dispose();
            _daemon = null;
        }
    }
}
