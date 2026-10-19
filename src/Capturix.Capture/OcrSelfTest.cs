using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Capturix.Ocr;

namespace Capturix.Capture;

/// <summary>
/// Headless OCR check on synthetic images (no screen content is read):
/// Capturix.exe --selftest-ocr &lt;outdir&gt;
/// </summary>
internal static class OcrSelfTest
{
    private sealed record Sample(string Name, string[] Lines, float FontPt, Color Fore, Color Back, string Font);

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new List<string>();
        int failures = 0;

        var languages = OcrService.AvailableLanguages;
        report.Add($"Idiomas OCR instalados: {(languages.Count == 0 ? "(ninguno)" : string.Join(", ", languages))}");
        report.Add($"Tamaño máximo de imagen: {OcrService.MaxImageDimension} px");
        if (languages.Count == 0)
        {
            report.Add("FALLO: no hay idiomas de OCR");
            File.WriteAllLines(Path.Combine(outDir, "ocr-report.txt"), report);
            return 1;
        }

        var samples = new[]
        {
            // Typical UI text at 100 % scaling: small, ClearType-like, dark on light.
            new Sample("ui-small", new[] { "Archivo Editar Ver Ayuda", "Guardar cambios antes de salir" }, 9f, Color.Black, Color.White, "Segoe UI"),
            // Light text on a dark theme.
            new Sample("dark-theme", new[] { "Capturix captura texto", "Version 2026 build 6" }, 11f, Color.FromArgb(230, 230, 230), Color.FromArgb(32, 32, 32), "Segoe UI"),
            // Code-like text with digits and punctuation.
            new Sample("code", new[] { "int total = 42;", "return total * 3;" }, 12f, Color.FromArgb(20, 20, 120), Color.FromArgb(250, 250, 240), "Consolas"),
        };

        foreach (var sample in samples)
        {
            using var bitmap = Render(sample);
            bitmap.Save(Path.Combine(outDir, $"ocr-{sample.Name}.png"), ImageFormat.Png);
            var outcome = BitmapOcr.RecognizeAsync(bitmap).GetAwaiter().GetResult();

            // Compare word by word, ignoring case and punctuation the engine may drop or add.
            var expected = Words(string.Join(" ", sample.Lines));
            var found = Words(outcome.Text).ToHashSet();
            int hits = expected.Count(found.Contains);
            double ratio = (double)hits / expected.Count;
            bool boxesInside = outcome.Lines.All(l => l.X >= -1 && l.Y >= -1
                                                      && l.X + l.Width <= bitmap.Width + 1 && l.Y + l.Height <= bitmap.Height + 1);
            bool ok = ratio >= 0.8 && outcome.Lines.Count == sample.Lines.Length && boxesInside;
            if (!ok)
                failures++;

            report.Add($"{(ok ? "OK   " : "FALLO")} {sample.Name}: {hits}/{expected.Count} palabras, {outcome.Lines.Count} líneas " +
                       $"(esperadas {sample.Lines.Length}), cajas dentro: {boxesInside}, idioma {outcome.Language}");
            report.Add($"      leído: {outcome.Text.Replace(Environment.NewLine, " | ")}");
        }

        // An image without text must not invent any.
        using (var blank = new Bitmap(300, 120))
        {
            using (var g = Graphics.FromImage(blank))
                g.Clear(Color.FromArgb(200, 220, 240));
            var outcome = BitmapOcr.RecognizeAsync(blank).GetAwaiter().GetResult();
            bool ok = outcome.Lines.Count == 0;
            if (!ok)
                failures++;
            report.Add($"{(ok ? "OK   " : "FALLO")} imagen sin texto: {outcome.Lines.Count} líneas");
        }

        report.Add(failures == 0 ? "RESULTADO: todo correcto" : $"RESULTADO: {failures} fallos");
        File.WriteAllLines(Path.Combine(outDir, "ocr-report.txt"), report);
        return failures == 0 ? 0 : 1;
    }

    private static Bitmap Render(Sample sample)
    {
        using var font = new Font(sample.Font, sample.FontPt);
        int lineHeight = (int)Math.Ceiling(font.GetHeight(96)) + 6;
        var bitmap = new Bitmap(420, 16 + lineHeight * sample.Lines.Length, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(sample.Back);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(sample.Fore);
        for (int i = 0; i < sample.Lines.Length; i++)
            g.DrawString(sample.Lines[i], font, brush, 10, 8 + i * lineHeight);
        return bitmap;
    }

    private static List<string> Words(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(w => w.Length > 0)
            .ToList();
}
