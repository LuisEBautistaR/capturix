using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Capturix.Ocr;

/// <summary>A recognized line and its box in the caller's image coordinates.</summary>
public sealed record OcrLine(string Text, double X, double Y, double Width, double Height);

public sealed record OcrOutcome(string Text, IReadOnlyList<OcrLine> Lines, string Language);

/// <summary>
/// Text recognition with the OCR engine built into Windows (offline, free, uses the
/// recognizer languages installed with Windows language packs). It is good enough for screen
/// text and adds no dependency.
/// </summary>
public static class OcrService
{
    /// <summary>Screen text is small; recognition improves a lot when the image is enlarged first.</summary>
    public const int SmallImageThreshold = 1600;

    /// <summary>BCP-47 tags of the installed OCR languages (e.g. "es-ES", "en-US").</summary>
    public static IReadOnlyList<string> AvailableLanguages =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList();

    public static int MaxImageDimension => (int)OcrEngine.MaxImageDimension;

    /// <summary>
    /// Recognizes text in a BGRA image. <paramref name="scale"/> is how much the caller enlarged
    /// the pixels before passing them; boxes are mapped back to the original coordinates.
    /// <paramref name="language"/> null uses the user's preferred Windows languages.
    /// </summary>
    public static async Task<OcrOutcome> RecognizeAsync(byte[] bgra, int width, int height, double scale = 1.0, string? language = null)
    {
        var engine = (language is null
                         ? OcrEngine.TryCreateFromUserProfileLanguages()
                         : OcrEngine.TryCreateFromLanguage(new Language(language)))
                     ?? throw new InvalidOperationException(
                         "Windows no tiene ningún idioma de OCR instalado. Añade uno en Configuración > Hora e idioma > Idioma y región.");

        if (width > MaxImageDimension || height > MaxImageDimension)
            throw new ArgumentException($"La imagen supera el máximo del OCR de Windows ({MaxImageDimension} px por lado).");

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(bgra.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        var lines = new List<OcrLine>();
        foreach (var line in result.Lines)
        {
            // A line's box is the union of its word boxes.
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                x0 = Math.Min(x0, r.X);
                y0 = Math.Min(y0, r.Y);
                x1 = Math.Max(x1, r.X + r.Width);
                y1 = Math.Max(y1, r.Y + r.Height);
            }
            lines.Add(new OcrLine(line.Text, x0 / scale, y0 / scale, (x1 - x0) / scale, (y1 - y0) / scale));
        }

        string text = string.Join(Environment.NewLine, lines.Select(l => l.Text));
        return new OcrOutcome(text, lines, engine.RecognizerLanguage.LanguageTag);
    }

    /// <summary>Upscale factor callers should apply before recognition (2× for small screenshots).</summary>
    public static double SuggestedScale(int width, int height)
    {
        int longest = Math.Max(width, height);
        if (longest <= SmallImageThreshold && longest * 2 <= MaxImageDimension)
            return 2.0;
        return 1.0;
    }
}
