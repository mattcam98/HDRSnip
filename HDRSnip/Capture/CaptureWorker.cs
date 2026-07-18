using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HDRSnip.Capture;

/// <summary>
/// Runs DXGI FP16 capture in a child process so AccessViolation in DuplicateOutput1
/// cannot tear down the tray app. Parent falls back to GDI if the worker fails.
/// </summary>
public static class CaptureWorker
{
    public const string ArgName = "--capture-worker";

    public static bool TryRunAsWorker(string[] args)
    {
        // Args: --capture-worker <outputIndex> <outFile>
        if (args.Length < 3 || !args[0].Equals(ArgName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!int.TryParse(args[1], out int outputIndex))
            Environment.Exit(2);

        var outFile = args[2];
        try
        {
            var monitors = DxgiHdrCapture.EnumerateMonitors();
            var monitor = monitors.FirstOrDefault(m => m.OutputIndex == outputIndex)
                          ?? throw new InvalidOperationException($"Output {outputIndex} not found.");

            CapturedFrame? frame = null;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using var cap = new DxgiHdrCapture();
                    frame = cap.CaptureFp16ForWorker(monitor);
                }
                catch (Exception ex)
                {
                    error = ex;
                    try
                    {
                        var logDir = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                            "HDRSnip");
                        Directory.CreateDirectory(logDir);
                        File.AppendAllText(Path.Combine(logDir, "errors.log"),
                            $"[{DateTime.Now:o}] CaptureWorker: {ex}\n");
                    }
                    catch { /* ignore */ }
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = false;
            thread.Start();
            if (!thread.Join(TimeSpan.FromSeconds(12)))
                Environment.Exit(3);

            if (error is not null || frame is null)
                Environment.Exit(4);

            WriteFrame(outFile, frame);
            Environment.Exit(0);
        }
        catch
        {
            Environment.Exit(5);
        }

        return true;
    }

    public static CapturedFrame? TryCaptureOutOfProcess(MonitorInfo monitor)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            return null;

        var outFile = Path.Combine(Path.GetTempPath(), "HDRSnip", $"cap_{Guid.NewGuid():N}.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                ArgumentList = { ArgName, monitor.OutputIndex.ToString(), outFile },
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return null;

            // Cold start + FP16 duplicate can exceed 5s on first run.
            if (!proc.WaitForExit(20_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }

            if (proc.ExitCode != 0 || !File.Exists(outFile))
                return null;

            var frame = ReadFrame(outFile, monitor.Bounds);
            return frame;
        }
        catch (Exception ex)
        {
            App.LogCrash("CaptureWorker.Parent", ex);
            return null;
        }
        finally
        {
            try { if (File.Exists(outFile)) File.Delete(outFile); } catch { /* ignore */ }
        }
    }

    private static void WriteFrame(string path, CapturedFrame frame)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write(frame.Width);
        bw.Write(frame.Height);
        bw.Write(frame.WasHdr);
        bw.Write(frame.MonitorBounds.Left);
        bw.Write(frame.MonitorBounds.Top);
        bw.Write(frame.MonitorBounds.Width);
        bw.Write(frame.MonitorBounds.Height);
        var bytes = MemoryMarshal.AsBytes(frame.RgbaLinear.AsSpan());
        bw.Write(bytes.Length);
        bw.Write(bytes);
    }

    private static CapturedFrame ReadFrame(string path, System.Drawing.Rectangle fallbackBounds)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
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
            MonitorBounds = new System.Drawing.Rectangle(left, top, bw, bh),
            RgbaLinear = rgba
        };
    }
}
