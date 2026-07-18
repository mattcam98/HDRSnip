namespace HDRSnip.Capture;

/// <summary>Process-wide capture host owned by the tray app.</summary>
public static class CaptureHost
{
    public static CaptureDaemonClient? Daemon { get; private set; }

    public static void Start()
    {
        Daemon ??= new CaptureDaemonClient();
        // Warm the daemon off the UI thread so startup never blocks on DXGI/pipe.
        var client = Daemon;
        _ = Task.Run(() =>
        {
            try { client.EnsureStarted(); }
            catch (Exception ex) { App.LogCrash("CaptureHost.Start", ex); }
        });
    }

    public static void Stop()
    {
        Daemon?.Dispose();
        Daemon = null;
    }
}
