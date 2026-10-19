using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturix.Editor.Model;

/// <summary>Immutable BGRA32 copy of the base image, used for pixel effects such as pixelation.</summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    private readonly byte[] _pixels;

    public PixelBuffer(BitmapSource source)
    {
        var bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        Width = bgra.PixelWidth;
        Height = bgra.PixelHeight;
        Stride = Width * 4;
        _pixels = new byte[Stride * Height];
        bgra.CopyPixels(_pixels, Stride, 0);
    }

    /// <summary>
    /// Returns a full-resolution bitmap of <paramref name="region"/> where every
    /// <paramref name="block"/>×<paramref name="block"/> cell is filled with its average colour.
    /// </summary>
    public BitmapSource Pixelate(Int32Rect region, int block)
    {
        var output = new byte[region.Width * region.Height * 4];
        int outStride = region.Width * 4;

        for (int by = 0; by < region.Height; by += block)
        {
            int bh = Math.Min(block, region.Height - by);
            for (int bx = 0; bx < region.Width; bx += block)
            {
                int bw = Math.Min(block, region.Width - bx);
                long b = 0, g = 0, r = 0, a = 0;
                for (int y = 0; y < bh; y++)
                {
                    int row = (region.Y + by + y) * Stride + (region.X + bx) * 4;
                    for (int x = 0; x < bw; x++)
                    {
                        int i = row + x * 4;
                        b += _pixels[i];
                        g += _pixels[i + 1];
                        r += _pixels[i + 2];
                        a += _pixels[i + 3];
                    }
                }
                int n = bw * bh;
                byte cb = (byte)(b / n), cg = (byte)(g / n), cr = (byte)(r / n), ca = (byte)(a / n);
                for (int y = 0; y < bh; y++)
                {
                    int row = (by + y) * outStride + bx * 4;
                    for (int x = 0; x < bw; x++)
                    {
                        int i = row + x * 4;
                        output[i] = cb;
                        output[i + 1] = cg;
                        output[i + 2] = cr;
                        output[i + 3] = ca;
                    }
                }
            }
        }

        var bmp = BitmapSource.Create(region.Width, region.Height, 96, 96, PixelFormats.Bgra32, null, output, outStride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Clamps a rectangle in image coordinates to whole pixels inside the image.</summary>
    public Int32Rect ClampToImage(Rect rect)
    {
        int x0 = Math.Clamp((int)Math.Floor(rect.Left), 0, Width);
        int y0 = Math.Clamp((int)Math.Floor(rect.Top), 0, Height);
        int x1 = Math.Clamp((int)Math.Ceiling(rect.Right), 0, Width);
        int y1 = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, Height);
        return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
    }
}
