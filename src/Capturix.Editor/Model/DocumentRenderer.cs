using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturix.Editor.Model;

/// <summary>Draws a document and flattens it to a bitmap for saving or copying.</summary>
public static class DocumentRenderer
{
    public static void Render(DrawingContext dc, EditorDocument doc)
    {
        dc.DrawImage(doc.Image, new Rect(0, 0, doc.PixelWidth, doc.PixelHeight));
        var ctx = doc.RenderContext;
        foreach (var annotation in doc.Annotations)
            annotation.Render(dc, ctx);
    }

    /// <summary>Base image with all annotations burned in, at the image's native pixel size.</summary>
    public static BitmapSource Flatten(EditorDocument doc)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            Render(dc, doc);

        var target = new RenderTargetBitmap(doc.PixelWidth, doc.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static void SaveJpeg(BitmapSource image, string path, int quality = 92)
    {
        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        // JPEG has no alpha channel; screen captures are opaque, so dropping it is lossless.
        var opaque = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        encoder.Frames.Add(BitmapFrame.Create(opaque));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
