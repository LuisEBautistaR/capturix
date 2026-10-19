using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// The Capturix logo: a rounded tile with a blue→violet gradient, white viewfinder corners
// (capture), a "C" and a coral record dot in its opening (record). Defined once here and
// exported as SVG, PNGs and a multi-size ICO so every copy matches.

string outDir = args.Length > 0 ? args[0] : Path.Combine("assets", "logo");
Directory.CreateDirectory(outDir);

var full = new LogoSpec(CornerStroke: 18, ShowC: true, DotX: 163, DotRadius: 10);
// Below 32 px the "C" turns to mush: keep bold corners and a centred record dot.
var small = new LogoSpec(CornerStroke: 28, ShowC: false, DotX: 128, DotRadius: 24);

File.WriteAllText(Path.Combine(outDir, "capturix-logo.svg"), Svg(full), new UTF8Encoding(false));
File.WriteAllText(Path.Combine(outDir, "capturix-logo-small.svg"), Svg(small), new UTF8Encoding(false));

foreach (int size in new[] { 64, 128, 256, 512, 1024 })
    File.WriteAllBytes(Path.Combine(outDir, $"capturix-logo-{size}.png"), Png(Render(full, size)));
WriteIco(Path.Combine(outDir, "capturix.ico"),
    new[] { 16, 20, 24, 32, 40, 48, 64, 256 }.Select(s => (s, Render(s < 32 ? small : full, s))).ToList());
Console.WriteLine($"Logo generado en {Path.GetFullPath(outDir)}");

static BitmapSource Render(LogoSpec spec, int size)
{
    var visual = new DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        Draw(dc, spec);
        dc.Pop();
    }
    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    return bitmap;
}

static byte[] Png(BitmapSource image)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(image));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

/// <summary>
/// Classic ICO image: BITMAPINFOHEADER, 32-bit BGRA rows bottom-up, then the 1-bit AND mask.
/// Every icon reader understands it, unlike PNG frames below 256 px.
/// </summary>
static byte[] Dib(BitmapSource image)
{
    int size = image.PixelWidth;
    var straight = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); // ICO alpha is not premultiplied
    var pixels = new byte[size * size * 4];
    straight.CopyPixels(pixels, size * 4, 0);

    using var stream = new MemoryStream();
    using var w = new BinaryWriter(stream);
    w.Write(40); w.Write(size); w.Write(size * 2); // height covers colour + mask
    w.Write((ushort)1); w.Write((ushort)32); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
    for (int y = size - 1; y >= 0; y--)
        w.Write(pixels, y * size * 4, size * 4);
    int maskStride = (size + 31) / 32 * 4;
    w.Write(new byte[maskStride * size]); // all opaque in the mask; alpha does the work
    return stream.ToArray();
}

static void Draw(DrawingContext dc, LogoSpec spec)
{
    var gradient = new LinearGradientBrush(Logo.GradientStart, Logo.GradientEnd, new Point(0, 0), new Point(1, 1));
    dc.DrawRoundedRectangle(gradient, null, new Rect(8, 8, 240, 240), 56, 56);
    // Soft top highlight gives the tile some depth.
    var shine = new LinearGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90);
    dc.DrawRoundedRectangle(shine, null, new Rect(8, 8, 240, 120), 56, 56);

    var pen = new Pen(Brushes.White, spec.CornerStroke) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
    foreach (var corner in Logo.Corners)
        dc.DrawGeometry(null, pen, Geometry.Parse(corner));
    if (spec.ShowC)
        dc.DrawGeometry(null, new Pen(Brushes.White, 18) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geometry.Parse(Logo.CArc));
    dc.DrawEllipse(new SolidColorBrush(Logo.Dot), null, new Point(spec.DotX, 128), spec.DotRadius, spec.DotRadius);
}

static string Svg(LogoSpec spec)
{
    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    var inv = CultureInfo.InvariantCulture;
    var sb = new StringBuilder();
    sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 256 256\" width=\"256\" height=\"256\">");
    sb.AppendLine("  <title>Capturix</title>");
    sb.AppendLine("  <defs>");
    sb.AppendLine($"    <linearGradient id=\"bg\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"{Hex(Logo.GradientStart)}\"/><stop offset=\"1\" stop-color=\"{Hex(Logo.GradientEnd)}\"/></linearGradient>");
    sb.AppendLine("    <linearGradient id=\"shine\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"><stop offset=\"0\" stop-color=\"#fff\" stop-opacity=\"0.18\"/><stop offset=\"1\" stop-color=\"#fff\" stop-opacity=\"0\"/></linearGradient>");
    sb.AppendLine("  </defs>");
    sb.AppendLine("  <rect x=\"8\" y=\"8\" width=\"240\" height=\"240\" rx=\"56\" fill=\"url(#bg)\"/>");
    sb.AppendLine("  <rect x=\"8\" y=\"8\" width=\"240\" height=\"120\" rx=\"56\" fill=\"url(#shine)\"/>");
    sb.AppendLine($"  <g fill=\"none\" stroke=\"#fff\" stroke-width=\"{spec.CornerStroke.ToString(inv)}\" stroke-linecap=\"round\" stroke-linejoin=\"round\">");
    foreach (var corner in Logo.Corners)
        sb.AppendLine($"    <path d=\"{corner}\"/>");
    sb.AppendLine("  </g>");
    if (spec.ShowC)
        sb.AppendLine($"  <path d=\"{Logo.CArc}\" fill=\"none\" stroke=\"#fff\" stroke-width=\"18\" stroke-linecap=\"round\"/>");
    sb.AppendLine($"  <circle cx=\"{spec.DotX.ToString(inv)}\" cy=\"128\" r=\"{spec.DotRadius.ToString(inv)}\" fill=\"{Hex(Logo.Dot)}\"/>");
    sb.AppendLine("</svg>");
    return sb.ToString();
}

/// <summary>Multi-size ICO: classic bitmaps up to 64 px, PNG for 256 px (keeps the file small).</summary>
static void WriteIco(string path, List<(int Size, BitmapSource Image)> sources)
{
    var images = sources.Select(s => (s.Size, Data: s.Size >= 256 ? Png(s.Image) : Dib(s.Image))).ToList();
    using var stream = File.Create(path);
    using var w = new BinaryWriter(stream);
    w.Write((ushort)0);
    w.Write((ushort)1); // icon
    w.Write((ushort)images.Count);
    int offset = 6 + 16 * images.Count;
    foreach (var (size, png) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0); // no palette
        w.Write((byte)0);
        w.Write((ushort)1); // planes
        w.Write((ushort)32); // bits per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in images)
        w.Write(png);
}

internal sealed record LogoSpec(double CornerStroke, bool ShowC, double DotX, double DotRadius);

internal static class Logo
{
    public static readonly Color GradientStart = Color.FromRgb(0x1E, 0xA0, 0xFF);
    public static readonly Color GradientEnd = Color.FromRgb(0x5B, 0x3D, 0xF5);
    public static readonly Color Dot = Color.FromRgb(0xFF, 0x5A, 0x5F);

    /// <summary>Viewfinder corners on a 256×256 grid (SVG / WPF path syntax).</summary>
    public static readonly string[] Corners =
    {
        "M60 100 V80 Q60 60 80 60 H100",
        "M156 60 H176 Q196 60 196 80 V100",
        "M196 156 V176 Q196 196 176 196 H156",
        "M100 196 H80 Q60 196 60 176 V156",
    };

    /// <summary>A "C" of radius 32 around the centre, open towards the record dot.</summary>
    public const string CArc = "M150.6 105.4 A32 32 0 1 0 150.6 150.6";
}
