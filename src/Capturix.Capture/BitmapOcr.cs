using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Capturix.Ocr;

namespace Capturix.Capture;

/// <summary>Runs the Windows OCR engine on a GDI+ bitmap.</summary>
internal static class BitmapOcr
{
    public static async Task<OcrOutcome> RecognizeAsync(Bitmap image)
    {
        double scale = OcrService.SuggestedScale(image.Width, image.Height);
        Bitmap source = scale == 1.0 ? image : Upscale(image, scale);
        try
        {
            var (pixels, width, height) = ToBgra(source);
            return await Task.Run(() => OcrService.RecognizeAsync(pixels, width, height, scale));
        }
        finally
        {
            if (!ReferenceEquals(source, image))
                source.Dispose();
        }
    }

    /// <summary>Small screen text reads much better enlarged with a smooth filter.</summary>
    private static Bitmap Upscale(Bitmap image, double scale)
    {
        var big = new Bitmap((int)(image.Width * scale), (int)(image.Height * scale), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(big);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(image, 0, 0, big.Width, big.Height);
        return big;
    }

    /// <summary>Format32bppArgb is stored as B, G, R, A in memory — exactly what the engine wants.</summary>
    private static (byte[] Pixels, int Width, int Height) ToBgra(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height, rowBytes = width * 4;
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[rowBytes * height];
            for (int y = 0; y < height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * rowBytes, rowBytes);
            return (pixels, width, height);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
