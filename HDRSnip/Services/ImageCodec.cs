using System.IO;
using System.Windows.Media.Imaging;

namespace HDRSnip.Services;

/// <summary>Bitmap encoding for the clipboard, saved captures and the toast thumbnail.</summary>
public static class ImageCodec
{
    public static byte[] EncodePng(BitmapSource image)
    {
        using var stream = new MemoryStream();
        Encode(new PngBitmapEncoder(), image, stream);
        return stream.ToArray();
    }

    /// <summary>Writes the image in the format the extension names, PNG for anything unrecognised.</summary>
    public static void Save(BitmapSource image, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = 95 },
            ".bmp" => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };

        using var stream = File.Create(path);
        Encode(encoder, image, stream);
    }

    private static void Encode(BitmapEncoder encoder, BitmapSource image, Stream stream)
    {
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
    }
}
