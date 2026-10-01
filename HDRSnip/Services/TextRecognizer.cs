using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace HDRSnip.Services;

/// <summary>
/// Reads the text in a capture with the OCR engine built into Windows. It runs
/// entirely on this machine, using the languages installed for the user.
/// </summary>
public static class TextRecognizer
{
    /// <summary>The recognised lines, an empty string if there is no text, or null if Windows has no OCR language installed.</summary>
    public static async Task<string?> RecognizeAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return null;

        image = ImageCodec.Thumbnail(image, (int)OcrEngine.MaxImageDimension);
        if (image.Format != PixelFormats.Bgra32)
            image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);

        int width = image.PixelWidth, height = image.PixelHeight;
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(line => line.Text));
    }
}
