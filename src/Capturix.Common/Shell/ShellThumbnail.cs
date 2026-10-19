using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Capturix.Common.Shell;

/// <summary>
/// Explorer-style thumbnails for any file (video frames for .mp4, first frame for .gif) via
/// IShellItemImageFactory. Returns an HBITMAP the caller must free with <see cref="DeleteBitmap"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellThumbnail
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    private const int SIIGBF_RESIZETOFIT = 0x00;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>HBITMAP of roughly <paramref name="size"/> pixels, or IntPtr.Zero if unavailable.</summary>
    public static IntPtr GetHBitmap(string path, int size)
    {
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
        if (hr < 0)
            return IntPtr.Zero;
        try
        {
            return factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_RESIZETOFIT | SIIGBF_BIGGERSIZEOK, out var hbitmap) < 0
                ? IntPtr.Zero
                : hbitmap;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    public static void DeleteBitmap(IntPtr hbitmap)
    {
        if (hbitmap != IntPtr.Zero)
            DeleteObject(hbitmap);
    }
}
