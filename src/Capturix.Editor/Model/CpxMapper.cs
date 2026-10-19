using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturix.Common.Format;

namespace Capturix.Editor.Model;

/// <summary>Converts between the editor's WPF model and the UI-free .cpx file schema.</summary>
public static class CpxMapper
{
    private const int ThumbnailMaxSide = 320;

    public static EditorDocument Load(string path)
    {
        var file = CpxFile.Read(path);
        var image = DecodePng(file.ImagePng);
        var document = new EditorDocument(image, path)
        {
            DocumentPath = path,
            Metadata = file.Metadata,
            PageId = file.Page.Id,
            NextStepNumber = file.Page.NextStepNumber,
        };
        foreach (var dto in file.Page.Annotations)
        {
            if (FromDto(dto) is { } annotation)
                document.Add(annotation);
        }
        return document;
    }

    public static void Save(EditorDocument document, string path)
    {
        var file = new CpxFile
        {
            Metadata = document.Metadata,
            Page = new CpxPage
            {
                Id = document.PageId,
                Width = document.PixelWidth,
                Height = document.PixelHeight,
                NextStepNumber = document.NextStepNumber,
                Annotations = document.Annotations.Select(ToDto).ToList(),
            },
            ImagePng = document.GetImagePng(),
            ThumbnailPng = RenderThumbnail(document),
        };
        file.Write(path);
    }

    public static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    private static BitmapSource DecodePng(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Flattened, downscaled preview shown in the library tray.</summary>
    private static byte[] RenderThumbnail(EditorDocument document)
    {
        double scale = Math.Min(1.0, (double)ThumbnailMaxSide / Math.Max(document.PixelWidth, document.PixelHeight));
        int w = Math.Max(1, (int)(document.PixelWidth * scale));
        int h = Math.Max(1, (int)(document.PixelHeight * scale));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            DocumentRenderer.Render(dc, document);
            dc.Pop();
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return EncodePng(target);
    }

    // ---- annotation <-> DTO ------------------------------------------------------------------

    private static AnnotationDto ToDto(Annotation a)
    {
        AnnotationDto dto = a switch
        {
            ArrowAnnotation x => TwoPoint(new ArrowDto(), x),
            LineAnnotation x => TwoPoint(new LineDto(), x),
            RectangleAnnotation x => TwoPoint(new RectangleDto(), x),
            EllipseAnnotation x => TwoPoint(new EllipseDto(), x),
            HighlightAnnotation x => TwoPoint(new HighlightDto(), x),
            PixelateAnnotation x => TwoPoint(new PixelateDto(), x),
            StampAnnotation x => TwoPoint(new StampDto { Emoji = x.Emoji }, x),
            StepAnnotation x => new StepDto { X = x.Center.X, Y = x.Center.Y, Number = x.Number },
            TextAnnotation x => new TextDto { X = x.Position.X, Y = x.Position.Y, Text = x.Text },
            PenAnnotation x => new PenDto { Points = x.Points.SelectMany(p => new[] { p.X, p.Y }).ToList() },
            _ => throw new NotSupportedException($"Anotación sin formato de archivo: {a.GetType().Name}"),
        };
        dto.Color = ToHex(a.Color);
        dto.Thickness = a.Thickness;
        return dto;
    }

    private static T TwoPoint<T>(T dto, TwoPointAnnotation a) where T : TwoPointDto
    {
        dto.X1 = a.Start.X;
        dto.Y1 = a.Start.Y;
        dto.X2 = a.End.X;
        dto.Y2 = a.End.Y;
        return dto;
    }

    /// <summary>Returns null for annotation types written by a newer version, so old builds still open the file.</summary>
    private static Annotation? FromDto(AnnotationDto dto)
    {
        Annotation? a = dto switch
        {
            ArrowDto x => TwoPoint(new ArrowAnnotation(), x),
            LineDto x => TwoPoint(new LineAnnotation(), x),
            RectangleDto x => TwoPoint(new RectangleAnnotation(), x),
            EllipseDto x => TwoPoint(new EllipseAnnotation(), x),
            HighlightDto x => TwoPoint(new HighlightAnnotation(), x),
            PixelateDto x => TwoPoint(new PixelateAnnotation(), x),
            StampDto x => TwoPoint(new StampAnnotation { Emoji = string.IsNullOrEmpty(x.Emoji) ? EmojiRenderer.DefaultStamp : x.Emoji }, x),
            StepDto x => new StepAnnotation { Center = new Point(x.X, x.Y), Number = x.Number },
            TextDto x => new TextAnnotation { Position = new Point(x.X, x.Y), Text = x.Text },
            PenDto x => PenFrom(x),
            _ => null,
        };
        if (a is null)
            return null;
        a.Color = FromHex(dto.Color);
        a.Thickness = dto.Thickness;
        return a;
    }

    private static TwoPointAnnotation TwoPoint(TwoPointAnnotation a, TwoPointDto dto)
    {
        a.Start = new Point(dto.X1, dto.Y1);
        a.End = new Point(dto.X2, dto.Y2);
        return a;
    }

    private static PenAnnotation PenFrom(PenDto dto)
    {
        var pen = new PenAnnotation();
        for (int i = 0; i + 1 < dto.Points.Count; i += 2)
            pen.Points.Add(new Point(dto.Points[i], dto.Points[i + 1]));
        return pen;
    }

    private static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Color FromHex(string hex)
    {
        string h = hex.TrimStart('#');
        if (h.Length == 6)
            h = "FF" + h;
        if (h.Length != 8 || !uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v))
            return Colors.Red;
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
