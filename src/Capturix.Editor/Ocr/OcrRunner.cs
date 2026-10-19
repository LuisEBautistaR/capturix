using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturix.Ocr;

namespace Capturix.Editor.Ocr;

/// <summary>Bridges WPF bitmaps to the Windows OCR engine.</summary>
public static class OcrRunner
{
    /// <summary>
    /// Reads the text of <paramref name="image"/>, or of <paramref name="zone"/> within it.
    /// Line boxes come back in the image's pixel coordinates.
    /// </summary>
    public static async Task<OcrOutcome> RecognizeAsync(BitmapSource image, Int32Rect? zone = null)
    {
        BitmapSource source = zone is { } z ? new CroppedBitmap(image, z) : image;
        double scale = OcrService.SuggestedScale(source.PixelWidth, source.PixelHeight);
        if (scale != 1.0)
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = source.PixelWidth, height = source.PixelHeight;
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);

        // The engine is agile; keep the UI thread free while it works. Nothing below touches WPF,
        // so the continuation need not return to the UI thread (callers may block on it).
        var outcome = await Task.Run(() => OcrService.RecognizeAsync(pixels, width, height, scale)).ConfigureAwait(false);
        if (zone is not { } offset)
            return outcome;
        var lines = outcome.Lines.Select(l => l with { X = l.X + offset.X, Y = l.Y + offset.Y }).ToList();
        return outcome with { Lines = lines };
    }
}
