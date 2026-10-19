using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text;
using Capturix.Core.Capture;

namespace Capturix.Capture;

/// <summary>
/// Headless check of <see cref="FrameStitcher"/>: Capturix.exe --selftest-scroll outdir.
/// Simulates a window with a fixed header and footer, a moving scrollbar thumb and blank
/// gaps in the content, scrolled by irregular steps; the stitched result must equal the page.
/// </summary>
internal static class ScrollSelfTest
{
    private const int Width = 700;
    private const int Header = 56;
    private const int Footer = 34;
    private const int ViewHeight = 420; // content viewport between header and footer
    private const int PageHeight = 3000;

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var report = new StringBuilder();
        bool ok = true;
        void Check(bool condition, string description)
        {
            report.AppendLine($"{(condition ? "OK  " : "FAIL")} {description}");
            ok &= condition;
        }

        try
        {
            using var page = DrawPage();
            int[] steps = { 230, 231, 180, 260, 120, 237, 250, 199, 240, 222, 260, 210, 233 };

            var offsets = new List<int> { 0 };
            foreach (int s in steps)
                offsets.Add(Math.Min(offsets[^1] + s, PageHeight - ViewHeight));
            offsets.Add(offsets[^1]); // end reached: two identical frames
            offsets = offsets.Distinct().Append(offsets[^1]).ToList();

            using var stitcher = new FrameStitcher();
            var outcomes = new List<StitchOutcome>();
            foreach (int offset in offsets)
            {
                using var frame = DrawFrame(page, offset);
                outcomes.Add(stitcher.Add(frame).Outcome);
            }
            report.AppendLine("Resultados: " + string.Join(", ", outcomes));

            int appended = outcomes.Count(o => o == StitchOutcome.Appended);
            Check(outcomes[0] == StitchOutcome.First, "El primer fotograma se acepta");
            Check(appended == offsets.Distinct().Count() - 1, $"Todos los desplazamientos se unen ({appended})");
            Check(outcomes[^1] == StitchOutcome.NoMovement, "El final de la página se detecta como sin movimiento");

            using var result = stitcher.Compose();
            int finalOffset = offsets[^1];
            int expectedHeight = Header + finalOffset + ViewHeight + Footer;
            Check(result.Height == expectedHeight, $"Altura {result.Height} px (esperada {expectedHeight})");
            result.Save(Path.Combine(outDir, "scroll_result.png"), ImageFormat.Png);

            using var expected = DrawFrameTall(page, finalOffset + ViewHeight);
            expected.Save(Path.Combine(outDir, "scroll_expected.png"), ImageFormat.Png);
            int diff = CountDifferentPixels(result, expected, Width - FrameStitcher.IgnoredRightColumns);
            Check(diff == 0, $"El resultado coincide píxel a píxel con la página (diferencias: {diff})");
        }
        catch (Exception ex)
        {
            report.AppendLine($"FAIL excepción: {ex}");
            ok = false;
        }

        report.AppendLine(ok ? "RESULTADO: OK" : "RESULTADO: FALLÓ");
        File.WriteAllText(Path.Combine(outDir, "scroll_selftest.txt"), report.ToString());
        return ok ? 0 : 1;
    }

    /// <summary>Tall content: numbered text lines with irregular blank gaps.</summary>
    private static Bitmap DrawPage()
    {
        var page = new Bitmap(Width, PageHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(page);
        g.Clear(Color.White);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        using var font = new Font("Segoe UI", 11f);
        var rnd = new Random(7);
        int y = 8;
        for (int i = 0; y < PageHeight - 30; i++)
        {
            g.DrawString($"Párrafo {i}: contenido de ejemplo para la captura con desplazamiento — {rnd.Next(100000)}",
                font, Brushes.Black, 16, y);
            y += 24 + (i % 9 == 0 ? 60 : 0); // occasional large blank gaps
            if (i % 13 == 0)
            {
                using var brush = new LinearGradientBrush(new Rectangle(16, y, 400, 40), Color.SteelBlue, Color.Orange, 0f);
                g.FillRectangle(brush, 16, y, 400, 40);
                y += 52;
            }
        }
        return page;
    }

    /// <summary>One screen of the simulated window at a scroll offset.</summary>
    private static Bitmap DrawFrame(Bitmap page, int offset)
    {
        var frame = new Bitmap(Width, Header + ViewHeight + Footer, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(frame);
        DrawChrome(g, frame.Height);
        g.DrawImage(page, new Rectangle(0, Header, Width, ViewHeight), new Rectangle(0, offset, Width, ViewHeight), GraphicsUnit.Pixel);
        // Scrollbar whose thumb moves with the offset (must be ignored by the matcher).
        g.FillRectangle(Brushes.Gainsboro, Width - 16, Header, 16, ViewHeight);
        int thumb = Header + (int)((double)offset / PageHeight * ViewHeight);
        g.FillRectangle(Brushes.DimGray, Width - 14, thumb, 12, 60);
        return frame;
    }

    /// <summary>What a perfect stitch looks like: header, all content up to <paramref name="contentBottom"/>, footer.</summary>
    private static Bitmap DrawFrameTall(Bitmap page, int contentBottom)
    {
        var tall = new Bitmap(Width, Header + contentBottom + Footer, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(tall);
        DrawChrome(g, tall.Height);
        g.DrawImage(page, new Rectangle(0, Header, Width, contentBottom), new Rectangle(0, 0, Width, contentBottom), GraphicsUnit.Pixel);
        return tall;
    }

    private static void DrawChrome(Graphics g, int height)
    {
        using var header = new SolidBrush(Color.FromArgb(40, 44, 52));
        g.FillRectangle(header, 0, 0, Width, Header);
        using var font = new Font("Segoe UI", 12f, FontStyle.Bold);
        g.DrawString("Cabecera fija — Ventana de prueba", font, Brushes.White, 14, 16);
        using var footer = new SolidBrush(Color.FromArgb(230, 230, 236));
        g.FillRectangle(footer, 0, height - Footer, Width, Footer);
        using var small = new Font("Segoe UI", 9f);
        g.DrawString("Barra de estado fija", small, Brushes.DimGray, 14, height - Footer + 9);
    }

    private static int CountDifferentPixels(Bitmap a, Bitmap b, int width)
    {
        if (a.Height != b.Height) return int.MaxValue;
        int diff = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < width; x++)
                if (a.GetPixel(x, y).ToArgb() != b.GetPixel(x, y).ToArgb())
                    diff++;
        return diff;
    }
}
