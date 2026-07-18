using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HDRSnip.Services;

/// <summary>
/// Windows toast notifications. Body click opens the last capture in the editor.
/// </summary>
public static class ToastNotificationService
{
    public const string ActionOpenEditor = "openEditor";

    public static event Action? OpenEditorRequested;

    public static void Initialize()
    {
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    public static void ShowCaptureCopied(bool wasHdr, int width, int height, string? previewPngPath)
    {
        try
        {
            var builder = new ToastContentBuilder()
                .AddArgument("action", ActionOpenEditor)
                .AddText("Screenshot copied")
                .AddText(wasHdr
                    ? $"{width}×{height} · HDR → SDR · Click to edit or save"
                    : $"{width}×{height} · Click to edit or save");

            if (!string.IsNullOrEmpty(previewPngPath) && File.Exists(previewPngPath))
            {
                builder.AddInlineImage(new Uri(previewPngPath));
            }

            builder.Show();
        }
        catch
        {
            // Caller falls back to tray balloon.
            throw;
        }
    }

    public static string? WriteTempPreview(BitmapSource image)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "HDRSnip");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "last-capture.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var fs = File.Create(path);
            encoder.Save(fs);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("action", out string action) || action != ActionOpenEditor)
            return;

        var app = System.Windows.Application.Current;
        if (app is null) return;

        app.Dispatcher.BeginInvoke(() => OpenEditorRequested?.Invoke());
    }
}
