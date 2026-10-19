using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturix.Editor.Model;

namespace Capturix.Editor;

/// <summary>
/// Headless check of the document model and renderer. Writes the flattened result to
/// <c>out.png</c> and a report to <c>out.png.txt</c>; exit code 0 means every check passed.
/// </summary>
internal static class SelfTest
{
    public static int Run(string inputPath, string outputPath)
    {
        var report = new StringBuilder();
        bool ok = true;
        void Check(bool condition, string description)
        {
            report.AppendLine($"{(condition ? "OK  " : "FAIL")} {description}");
            ok &= condition;
        }

        try
        {
            var doc = EditorDocument.Load(inputPath);
            double w = doc.PixelWidth, h = doc.PixelHeight;
            report.AppendLine($"Imagen: {w}x{h}");

            var red = Color.FromRgb(0xE8, 0x1E, 0x1E);
            var arrowStart = new Point(w * 0.10, h * 0.50);
            var arrowEnd = new Point(w * 0.45, h * 0.50);
            var pixelRect = new Rect(w * 0.55, h * 0.10, w * 0.30, h * 0.30);

            Annotation[] annotations =
            {
                new ArrowAnnotation { Start = arrowStart, End = arrowEnd, Color = red, Thickness = 6 },
                new LineAnnotation { Start = new Point(w * 0.1, h * 0.9), End = new Point(w * 0.4, h * 0.8), Color = Colors.Blue },
                new RectangleAnnotation { Start = new Point(w * 0.05, h * 0.05), End = new Point(w * 0.30, h * 0.30), Color = Colors.Orange },
                new EllipseAnnotation { Start = new Point(w * 0.6, h * 0.6), End = new Point(w * 0.9, h * 0.9), Color = Colors.Green },
                new HighlightAnnotation { Start = new Point(w * 0.1, h * 0.35), End = new Point(w * 0.5, h * 0.42), Color = Colors.Yellow },
                new PixelateAnnotation { Start = pixelRect.TopLeft, End = pixelRect.BottomRight, Thickness = 6 },
                new StepAnnotation { Center = new Point(w * 0.5, h * 0.7), Number = doc.NextStepNumber++ },
                new TextAnnotation { Position = new Point(w * 0.12, h * 0.55), Text = "Capturix autoprueba", Color = Colors.White },
            };
            var pen = new PenAnnotation { Color = Colors.Purple };
            for (int i = 0; i <= 20; i++)
                pen.Points.Add(new Point(w * (0.6 + i * 0.01), h * (0.5 + Math.Sin(i / 3.0) * 0.03)));

            foreach (var a in annotations.Append(pen))
            {
                doc.Checkpoint();
                doc.Add(a);
            }
            Check(doc.Annotations.Count == 9, $"9 anotaciones añadidas (hay {doc.Annotations.Count})");

            doc.Undo();
            Check(doc.Annotations.Count == 8, "Deshacer quita la última anotación");
            doc.Redo();
            Check(doc.Annotations.Count == 9, "Rehacer la recupera");
            Check(doc.Annotations[^1] is PenAnnotation, "La anotación rehecha es el trazo a mano alzada");

            var flat = DocumentRenderer.Flatten(doc);
            var px = new PixelReader(flat);
            var mid = new Point((arrowStart.X + arrowEnd.X) / 2, arrowStart.Y);
            var c = px.At((int)mid.X, (int)mid.Y);
            Check(c.R > 180 && c.G < 90 && c.B < 90, $"Centro de la flecha es rojo (#{c.R:X2}{c.G:X2}{c.B:X2})");

            var region = doc.Pixels.ClampToImage(pixelRect);
            int block = ((PixelateAnnotation)annotations[5]).BlockSize;
            var b0 = px.At(region.X + 1, region.Y + 1);
            var b1 = px.At(region.X + block - 2, region.Y + block - 2);
            Check(b0 == b1, $"El pixelado forma bloques uniformes de {block} px");

            var arrow = (ArrowAnnotation)doc.Annotations[0];
            var cropRect = new Int32Rect((int)(w * 0.05), (int)(h * 0.05), (int)(w * 0.9), (int)(h * 0.9));
            var before = arrow.Start;
            doc.Crop(cropRect);
            arrow = (ArrowAnnotation)doc.Annotations[0];
            Check(doc.PixelWidth == cropRect.Width && doc.PixelHeight == cropRect.Height,
                $"Recorte cambia el tamaño a {doc.PixelWidth}x{doc.PixelHeight}");
            Check(Math.Abs(arrow.Start.X - (before.X - cropRect.X)) < 0.01, "Recorte desplaza las anotaciones");
            doc.Undo();
            Check(doc.PixelWidth == (int)w, "Deshacer revierte el recorte");

            DocumentRenderer.SavePng(DocumentRenderer.Flatten(doc), outputPath);
            Check(File.Exists(outputPath), $"Exportado {outputPath}");

            // .cpx round trip: everything must come back editable and identical.
            string cpxPath = Path.ChangeExtension(outputPath, ".cpx");
            CpxMapper.Save(doc, cpxPath);
            var reloaded = EditorDocument.Load(cpxPath);
            Check(reloaded.DocumentPath == cpxPath, ".cpx recargado queda como destino de autoguardado");
            Check(reloaded.PixelWidth == doc.PixelWidth && reloaded.PixelHeight == doc.PixelHeight, ".cpx conserva el tamaño de la imagen");
            Check(reloaded.Annotations.Select(a => a.GetType()).SequenceEqual(doc.Annotations.Select(a => a.GetType())),
                $".cpx conserva las {reloaded.Annotations.Count} anotaciones y su orden");
            var reArrow = (ArrowAnnotation)reloaded.Annotations[0];
            Check(reArrow.Start == ((ArrowAnnotation)doc.Annotations[0]).Start && reArrow.Color == red,
                ".cpx conserva posición y color de la flecha");
            Check(reloaded.Annotations.OfType<TextAnnotation>().Single().Text == "Capturix autoprueba", ".cpx conserva el texto");
            Check(reloaded.Annotations.OfType<PenAnnotation>().Single().Points.Count == 21, ".cpx conserva los 21 puntos del trazo");
            Check(reloaded.NextStepNumber == doc.NextStepNumber, ".cpx conserva el contador de pasos");
            Check(Capturix.Common.Format.CpxFile.ReadThumbnail(cpxPath) is { Length: > 0 }, ".cpx incluye miniatura");

            var original = DocumentRenderer.Flatten(doc);
            var roundTrip = DocumentRenderer.Flatten(reloaded);
            var a1 = new PixelReader(original).At((int)mid.X, (int)mid.Y);
            var a2 = new PixelReader(roundTrip).At((int)mid.X, (int)mid.Y);
            Check(a1 == a2, "La imagen renderizada tras recargar es idéntica en la flecha");

            CheckOcr(Check, report);
            CheckStamps(Check, report, Path.ChangeExtension(outputPath, null));
        }
        catch (Exception ex)
        {
            report.AppendLine($"FAIL excepción: {ex}");
            ok = false;
        }

        report.AppendLine(ok ? "RESULTADO: OK" : "RESULTADO: FALLÓ");
        File.WriteAllText(outputPath + ".txt", report.ToString());
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Emoji stamps: every offered emoji has colour layers, a stamp renders in colour (WPF alone
    /// would draw a black outline), and stamps survive the .cpx round trip with the right version.
    /// </summary>
    private static void CheckStamps(Action<bool, string> check, StringBuilder report, string outBase)
    {
        check(EmojiRenderer.HasColor, "La fuente de emoji de Windows tiene capas de color");
        var flat = EmojiRenderer.Stamps.Where(e => EmojiRenderer.LayerCount(e) == 0).ToList();
        check(flat.Count == 0, $"Los {EmojiRenderer.Stamps.Length} sellos tienen color" + (flat.Count > 0 ? $" (sin color: {string.Join(" ", flat)})" : ""));

        var white = BitmapSource.Create(400, 200, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat((byte)0xFF, 400 * 200 * 4).ToArray(), 400 * 4);
        var doc = new EditorDocument(white);
        doc.Add(new StampAnnotation { Emoji = "😢", Start = new Point(20, 20), End = new Point(180, 180) });
        doc.Add(new StampAnnotation { Emoji = "👍", Start = new Point(220, 20), End = new Point(380, 180) });

        var px = new PixelReader(DocumentRenderer.Flatten(doc));
        int coloured = 0, inked = 0;
        for (int y = 20; y < 180; y += 4)
        {
            for (int x = 20; x < 180; x += 4)
            {
                var c = px.At(x, y);
                if (c.R > 200 && c.G > 140 && c.B < 120) coloured++; // the face's yellow
                if (c.R < 250 || c.G < 250 || c.B < 250) inked++;
            }
        }
        report.AppendLine($"     Sello 😢: {inked} muestras pintadas, {coloured} amarillas");
        check(coloured > inked / 3 && inked > 300, "El sello se dibuja a color (cara amarilla), no en contorno negro");

        string cpx = outBase + "-sellos.cpx";
        CpxMapper.Save(doc, cpx);
        var back = EditorDocument.Load(cpx);
        var stamps = back.Annotations.OfType<StampAnnotation>().ToList();
        check(stamps.Count == 2 && stamps[0].Emoji == "😢" && stamps[1].Emoji == "👍" && stamps[0].Start == new Point(20, 20),
            ".cpx conserva los sellos, su emoji y su posición");
        check(ReadFormatVersion(cpx) == Capturix.Common.Format.CpxIndex.FirstVersionWithStamps, "Un documento con sellos se guarda como formato 2");

        string plain = outBase + "-sin-sellos.cpx";
        var noStamps = new EditorDocument(white);
        noStamps.Add(new ArrowAnnotation { Start = new Point(10, 10), End = new Point(100, 100) });
        CpxMapper.Save(noStamps, plain);
        check(ReadFormatVersion(plain) == 1, "Sin sellos se sigue guardando como formato 1 (lo abre Capturix 1.0)");
    }

    private static int ReadFormatVersion(string cpx)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(cpx);
        using var stream = zip.GetEntry("index.json")!.Open();
        using var json = System.Text.Json.JsonDocument.Parse(stream);
        return json.RootElement.GetProperty("Version").GetInt32();
    }

    /// <summary>
    /// OCR on a synthetic document: text annotations are read, zone boxes come back in image
    /// coordinates, and pixelated text is no longer readable.
    /// </summary>
    private static void CheckOcr(Action<bool, string> check, StringBuilder report)
    {
        var white = BitmapSource.Create(640, 220, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat((byte)0xFF, 640 * 220 * 4).ToArray(), 640 * 4);
        var doc = new EditorDocument(white);
        doc.Add(new TextAnnotation { Position = new Point(20, 20), Text = "Factura numero 12345", Color = Colors.Black, Thickness = 2 });
        doc.Add(new TextAnnotation { Position = new Point(20, 120), Text = "Clave secreta oculta", Color = Colors.Black, Thickness = 2 });

        var whole = Ocr.OcrRunner.RecognizeAsync(DocumentRenderer.Flatten(doc)).GetAwaiter().GetResult();
        report.AppendLine($"     OCR imagen completa ({whole.Language}): {whole.Text.Replace(Environment.NewLine, " | ")}");
        check(whole.Text.Contains("Factura") && whole.Text.Contains("12345") && whole.Text.Contains("secreta"),
            "OCR lee el texto de toda la imagen");

        var zone = new Int32Rect(0, 100, 640, 120);
        var partial = Ocr.OcrRunner.RecognizeAsync(DocumentRenderer.Flatten(doc), zone).GetAwaiter().GetResult();
        check(partial.Text.Contains("secreta") && !partial.Text.Contains("Factura"), "OCR de una zona lee solo esa zona");
        check(partial.Lines.Count > 0 && partial.Lines.All(l => l.Y >= zone.Y && l.Y + l.Height <= zone.Y + zone.Height + 1),
            "Las cajas de la zona están en coordenadas de la imagen");

        doc.Add(new PixelateAnnotation { Start = new Point(0, 100), End = new Point(640, 220), Thickness = 10 });
        var hidden = Ocr.OcrRunner.RecognizeAsync(DocumentRenderer.Flatten(doc)).GetAwaiter().GetResult();
        check(hidden.Text.Contains("Factura") && !hidden.Text.Contains("secreta"), "El texto pixelado ya no es legible por OCR");
    }

    private sealed class PixelReader
    {
        private readonly byte[] _data;
        private readonly int _stride;

        public PixelReader(BitmapSource source)
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            _stride = bgra.PixelWidth * 4;
            _data = new byte[_stride * bgra.PixelHeight];
            bgra.CopyPixels(_data, _stride, 0);
        }

        public Color At(int x, int y)
        {
            int i = y * _stride + x * 4;
            return Color.FromArgb(_data[i + 3], _data[i + 2], _data[i + 1], _data[i]);
        }
    }
}
