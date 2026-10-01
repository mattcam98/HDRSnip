using System.IO;
using System.Windows.Media;
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
    private const int PreviewMaxEdge = 720;

    public static event Action? OpenEditorRequested;

    public static void Initialize() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public static void Shutdown() => ToastNotificationManagerCompat.OnActivated -= OnActivated;

    public static void ShowCapture(CaptureResult result, bool copied)
    {
        string detail = $"{result.Image.PixelWidth} × {result.Image.PixelHeight}";
        if (result.WasHdr)
            detail += " · HDR tone-mapped";
        detail += result.SaveFailed ? " · Auto-save failed, check the save folder" : " · Click to edit or save";

        var builder = new ToastContentBuilder()
            .AddArgument(ActionKey, OpenEditor)
            .AddText(Headline(result, copied))
            .AddText(detail);

        if (TryWritePreview(result.Image) is { } previewPath)
            builder.AddInlineImage(new Uri(previewPath));

        builder.Show();
    }

    /// <summary>What actually happened to the capture, for the toast and its fallbacks.</summary>
    public static string Headline(CaptureResult result, bool copied) =>
        copied ? "Screenshot copied"
        : result.SavedPath is not null ? "Screenshot saved"
        : "Screenshot captured";

    /// <summary>Writes the toast thumbnail, downscaled: it only has to look right at 360px.</summary>
    private static string? TryWritePreview(BitmapSource image)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "HDRSnip", "last-capture.png");
            ImageCodec.Save(Downscale(image, PreviewMaxEdge), path);
            return path;
        }
        catch (Exception ex)
        {
            App.LogError("ToastPreview", ex);
            return null;
        }
    }

    private static BitmapSource Downscale(BitmapSource source, int maxEdge)
    {
        int longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= maxEdge)
            return source;

        double scale = (double)maxEdge / longest;
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    private static void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        if (!arguments.TryGetValue(ActionKey, out string action) || action != OpenEditor)
            return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => OpenEditorRequested?.Invoke());
    }
}
