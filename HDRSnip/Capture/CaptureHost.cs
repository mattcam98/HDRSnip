namespace HDRSnip.Capture;

/// <summary>Owns the single capture daemon shared by the whole tray process.</summary>
public static class CaptureHost
{
    public static CaptureDaemonClient? Daemon { get; private set; }

    /// <summary>
    /// Warms the daemon on a background thread. Paying the process launch and the
    /// DXGI device and duplication setup now is what makes the first snip feel as
    /// fast as the tenth.
    /// </summary>
    public static void Start()
    {
        var client = Daemon ??= new CaptureDaemonClient();
        _ = Task.Run(() =>
        {
            try { client.Warm(); }
            catch (Exception ex) { App.LogError("CaptureHost", ex); }
        });
    }

    public static void Stop()
    {
        Daemon?.Dispose();
        Daemon = null;
    }
}
