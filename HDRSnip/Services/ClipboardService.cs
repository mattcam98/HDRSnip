using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HDRSnip.Services;

public static class ClipboardService
{
    /// <summary>
    /// Puts an image on the clipboard as a DIB plus PNG, so apps that prefer PNG
    /// keep exact pixels. Returns false if another app kept the clipboard locked.
    /// </summary>
    public static bool TryCopy(BitmapSource image, byte[]? png = null)
    {
        var data = new DataObject();
        data.SetImage(image);

        try
        {
            data.SetData("PNG", new MemoryStream(png ?? ImageCodec.EncodePng(image)), autoConvert: false);
        }
        catch (Exception ex)
        {
            // The DIB alone still pastes everywhere that matters.
            App.LogError("ClipboardPng", ex);
        }

        try
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (Exception ex)
        {
            App.LogError("Clipboard", ex);
            return false;
        }
    }
}
