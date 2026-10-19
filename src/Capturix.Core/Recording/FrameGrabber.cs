using System.Runtime.InteropServices;
using Capturix.Core.Interop;

namespace Capturix.Core.Recording;

/// <summary>
/// Grabs one screen rectangle repeatedly into a reusable top-down DIB section and returns
/// BGRA bytes, optionally with the mouse cursor drawn in. Not thread-safe; one per capture thread.
/// </summary>
internal sealed class FrameGrabber : IDisposable
{
    private readonly Rectangle _region;
    private readonly bool _drawCursor;
    private readonly IntPtr _screenDc;
    private readonly IntPtr _memDc;
    private readonly IntPtr _dib;
    private readonly IntPtr _oldBitmap;
    private readonly IntPtr _bits;

    public int Width => _region.Width;
    public int Height => _region.Height;
    public int Stride => _region.Width * 4;
    public int FrameBytes => Stride * _region.Height;

    public FrameGrabber(Rectangle region, bool drawCursor)
    {
        _region = region;
        _drawCursor = drawCursor;
        _screenDc = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(_screenDc);
        var header = new Native.BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = region.Width,
            biHeight = -region.Height, // negative: top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        _dib = Native.CreateDIBSection(_memDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception();
        _oldBitmap = Native.SelectObject(_memDc, _dib);
    }

    /// <summary>Copies the current screen content of the region into <paramref name="target"/>.</summary>
    public void Grab(byte[] target)
    {
        // Plain SRCCOPY: with DWM composition layered windows are captured anyway, and
        // CAPTUREBLT makes the mouse cursor flicker while recording.
        Native.BitBlt(_memDc, 0, 0, _region.Width, _region.Height, _screenDc, _region.X, _region.Y, Native.SRCCOPY);
        if (_drawCursor)
            DrawCursor();
        Marshal.Copy(_bits, target, 0, FrameBytes);
    }

    private void DrawCursor()
    {
        var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref info) || (info.flags & Native.CURSOR_SHOWING) == 0 || info.hCursor == IntPtr.Zero)
            return;
        if (!Native.GetIconInfo(info.hCursor, out var icon))
            return;
        try
        {
            int x = info.ptScreenPos.X - icon.xHotspot - _region.X;
            int y = info.ptScreenPos.Y - icon.yHotspot - _region.Y;
            Native.DrawIconEx(_memDc, x, y, info.hCursor, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
        }
        finally
        {
            // GetIconInfo hands us copies of the cursor bitmaps; they must be freed.
            if (icon.hbmMask != IntPtr.Zero) Native.DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) Native.DeleteObject(icon.hbmColor);
        }
    }

    public void Dispose()
    {
        Native.SelectObject(_memDc, _oldBitmap);
        Native.DeleteObject(_dib);
        Native.DeleteDC(_memDc);
        Native.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
