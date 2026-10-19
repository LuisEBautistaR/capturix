using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Capturix.Common.Format;

namespace Capturix.Core.Storage;

/// <summary>Writes captures into the library folder as editable .cpx documents.</summary>
public sealed class CaptureStore
{
    private const int ThumbnailMaxSide = 320;

    public string Folder { get; }

    public CaptureStore(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(Folder);
    }

    /// <summary>
    /// Saves <paramref name="image"/> as a new .cpx (and optionally a .png copy) and returns
    /// the .cpx path. Safe to call from a background thread with an image it owns.
    /// </summary>
    public string SaveCapture(Bitmap image, string source, bool pngCopy, DateTime? when = null)
    {
        var date = when ?? DateTime.Now;
        byte[] png = EncodePng(image);
        var document = new CpxFile
        {
            Metadata = new CpxMetadata { CaptureDate = date, Source = source },
            Page = new CpxPage { Width = image.Width, Height = image.Height },
            ImagePng = png,
            ThumbnailPng = CreateThumbnail(image),
        };

        string path = NextPath(date, CpxFile.Extension);
        document.Write(path);
        if (pngCopy)
            File.WriteAllBytes(Path.ChangeExtension(path, ".png"), png);
        return path;
    }

    private static byte[] EncodePng(Image image)
    {
        using var buffer = new MemoryStream();
        image.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    private static byte[] CreateThumbnail(Bitmap image)
    {
        double scale = Math.Min(1.0, (double)ThumbnailMaxSide / Math.Max(image.Width, image.Height));
        int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
        using var thumb = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(image, 0, 0, w, h);
        }
        return EncodePng(thumb);
    }

    /// <summary>A free, timestamped path in the library for a recording (".mp4", ".gif").</summary>
    public string NextRecordingPath(string extension) => NextPath(DateTime.Now, extension);

    private string NextPath(DateTime when, string extension)
    {
        string baseName = when.ToString("yyyy-MM-dd_HH-mm-ss");
        string path = Path.Combine(Folder, baseName + extension);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(Folder, $"{baseName}_{i}{extension}");
        return path;
    }
}
