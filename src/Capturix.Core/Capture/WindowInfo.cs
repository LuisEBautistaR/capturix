using Capturix.Core.Interop;

namespace Capturix.Core.Capture;

/// <summary>A visible top-level window that the user can pick in the region selector.</summary>
public sealed record WindowInfo(IntPtr Handle, string Title, string ClassName, Rectangle Bounds);

public static class WindowEnumerator
{
    /// <summary>
    /// Visible top-level windows in z-order (topmost first), excluding cloaked windows
    /// (e.g. UWP apps on other virtual desktops), tool windows and this process's own windows.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var result = new List<WindowInfo>();
        uint ownPid = (uint)Environment.ProcessId;

        Native.EnumWindows((hWnd, _) =>
        {
            if (!Native.IsWindowVisible(hWnd) || Native.IsIconic(hWnd) || Native.IsCloaked(hWnd))
                return true;

            Native.GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == ownPid)
                return true;

            long exStyle = Native.GetWindowLongPtr(hWnd, Native.GWL_EXSTYLE).ToInt64();
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0)
                return true;

            var bounds = Native.GetVisibleBounds(hWnd);
            if (bounds.Width < 8 || bounds.Height < 8)
                return true;

            result.Add(new WindowInfo(hWnd, Native.GetWindowTitle(hWnd), Native.GetWindowClass(hWnd), bounds));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>The topmost window containing <paramref name="screenPoint"/>, or null.</summary>
    public static WindowInfo? HitTest(IReadOnlyList<WindowInfo> windows, Point screenPoint)
    {
        foreach (var w in windows)
        {
            if (w.Bounds.Contains(screenPoint))
                return w;
        }
        return null;
    }
}
