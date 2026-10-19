using System.Drawing.Imaging;
using Capturix.Core.Interop;

namespace Capturix.Core.Capture;

/// <summary>
/// GDI-based still capture. The process runs Per-Monitor-V2 DPI aware, so every
/// coordinate here is in physical pixels of the virtual desktop.
/// </summary>
public static class ScreenCapture
{
    public static Rectangle VirtualScreen => SystemInformation.VirtualScreen;

    /// <summary>Copies an arbitrary rectangle of the virtual desktop.</summary>
    public static Bitmap CaptureRegion(Rectangle screenRect)
    {
        if (screenRect.Width <= 0 || screenRect.Height <= 0)
            throw new ArgumentException("La región de captura está vacía.", nameof(screenRect));

        var bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr bmpDc = g.GetHdc();
        try
        {
            // CAPTUREBLT includes layered (semi-transparent) windows such as tooltips and menus.
            // Graphics.CopyFromScreen rejects this flag combination, so BitBlt is called directly.
            if (!Native.BitBlt(bmpDc, 0, 0, screenRect.Width, screenRect.Height,
                    screenDc, screenRect.X, screenRect.Y, Native.SRCCOPY | Native.CAPTUREBLT))
            {
                throw new System.ComponentModel.Win32Exception();
            }
        }
        finally
        {
            g.ReleaseHdc(bmpDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
        return bmp;
    }

    /// <summary>All monitors as one image.</summary>
    public static Bitmap CaptureVirtualScreen() => CaptureRegion(VirtualScreen);

    /// <summary>The monitor that currently contains the mouse cursor.</summary>
    public static Bitmap CaptureMonitorUnderCursor()
    {
        Native.GetCursorPos(out var p);
        return CaptureRegion(Screen.FromPoint(new Point(p.X, p.Y)).Bounds);
    }

    /// <summary>
    /// Captures a window by copying its visible bounds from the screen. This keeps
    /// the window's real on-screen look (DWM effects, hardware-accelerated content)
    /// at the cost of including anything drawn on top of it.
    /// </summary>
    public static Bitmap CaptureWindow(IntPtr hWnd)
    {
        var bounds = Rectangle.Intersect(Native.GetVisibleBounds(hWnd), VirtualScreen);
        return CaptureRegion(bounds);
    }

    public static Bitmap CaptureForegroundWindow() => CaptureWindow(Native.GetForegroundWindow());

    /// <summary>Returns a copy of <paramref name="source"/> cropped to <paramref name="rect"/>.</summary>
    public static Bitmap Crop(Bitmap source, Rectangle rect)
    {
        rect.Intersect(new Rectangle(Point.Empty, source.Size));
        return source.Clone(rect, source.PixelFormat);
    }
}
