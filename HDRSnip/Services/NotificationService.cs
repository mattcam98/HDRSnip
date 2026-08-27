using System.IO;
using System.Windows.Media.Imaging;
using HDRSnip.Capture;
using Microsoft.Toolkit.Uwp.Notifications;

namespace HDRSnip.Services;

/// <summary>
/// Post-capture toast. Clicking the body reopens the capture in the editor,
/// which is the default flow: copy silently, offer the editor only if wanted.
/// </summary>
public static class NotificationService
{
    private const string ActionKey = "action";
    private const string OpenEditor = "openEditor";

    public static event Action? OpenEditorRequested;

    public static void Initialize() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public static void Shutdown() => ToastNotificationManagerCompat.OnActivated -= OnActivated;

    public static void ShowCaptureCopied(bool wasHdr, int width, int height, string? previewPath)
    {
        var builder = new ToastContentBuilder()
            .AddArgument(ActionKey, OpenEditor)
            .AddText("Screenshot copied")
            .AddText(wasHdr
                ? $"{width} × {height} · HDR tone-mapped · Click to edit or save"
                : $"{width} × {height} · Click to edit or save");

        if (!string.IsNullOrEmpty(previewPath) && File.Exists(previewPath))
            builder.AddInlineImage(new Uri(previewPath));

        builder.Show();
    }

    /// <summary>
    /// Writes the toast thumbnail. Downscaled deliberately — the clipboard and
    /// any saved PNG keep full resolution; this only has to look right at 360px.
    /// </summary>
    public static string? TryWritePreview(BitmapSource image)
    {
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "HDRSnip");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "last-capture.png");

            var preview = ToneMapper.ScaleBitmapMaxEdge(image, ToneMapper.ToastPreviewMaxEdge);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(preview));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch (Exception ex)
        {
            App.LogError("ToastPreview", ex);
            return null;
        }
    }

    private static void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        if (!arguments.TryGetValue(ActionKey, out string action) || action != OpenEditor)
            return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OpenEditorRequested?.Invoke());
    }
}
