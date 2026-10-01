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
    private const string Edit = "openEditor";
    private const string Save = "save";
    private const string Pin = "pin";
    private const int PreviewMaxEdge = 720;

    public static event Action<ToastAction>? ActionRequested;

    public static void Initialize() => ToastNotificationManagerCompat.OnActivated += OnActivated;

    public static void Shutdown() => ToastNotificationManagerCompat.OnActivated -= OnActivated;

    public static void ShowCapture(CaptureResult result, bool copied)
    {
        string detail = $"{result.Image.PixelWidth} × {result.Image.PixelHeight}";
        if (result.WasHdr)
            detail += " · HDR tone-mapped";
        detail += result.SaveFailed ? " · Auto-save failed, check the save folder" : " · Click to edit or save";

        var builder = new ToastContentBuilder()
            .AddArgument(ActionKey, Edit)
            .AddText(Headline(result, copied))
            .AddText(detail)
            .AddButton(new ToastButton().SetContent("Edit").AddArgument(ActionKey, Edit));

        // Nothing to offer when auto-save already wrote the file.
        if (result.SavedPath is null)
            builder.AddButton(new ToastButton().SetContent("Save").AddArgument(ActionKey, Save));

        builder.AddButton(new ToastButton().SetContent("Pin").AddArgument(ActionKey, Pin));

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
            ImageCodec.Save(ImageCodec.Thumbnail(image, PreviewMaxEdge), path);
            return path;
        }
        catch (Exception ex)
        {
            App.LogError("ToastPreview", ex);
            return null;
        }
    }

    /// <summary>A short confirmation with no actions, for work a toast button started.</summary>
    public static void ShowMessage(string title, string detail) =>
        new ToastContentBuilder().AddText(title).AddText(detail).Show();

    private static void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var arguments = ToastArguments.Parse(e.Argument);
        if (!arguments.TryGetValue(ActionKey, out string action) || ParseAction(action) is not { } requested)
            return;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ActionRequested?.Invoke(requested));
    }

    internal static ToastAction? ParseAction(string action) => action switch
    {
        Edit => ToastAction.Edit,
        Save => ToastAction.Save,
        Pin => ToastAction.Pin,
        _ => null
    };
}

public enum ToastAction
{
    Edit,
    Save,
    Pin
}
