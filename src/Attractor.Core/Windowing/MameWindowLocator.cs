using System.Diagnostics;
using System.Text;

namespace Attractor.Core.Windowing;

/// <summary>
/// Finds the MAME game window: the process's visible top-level window of
/// window class "MAME" (the class string is identical from 0.147 to current).
/// </summary>
public static class MameWindowLocator
{
    public static IntPtr FindGameWindow(int pid)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint windowPid);
            if (windowPid != (uint)pid || !NativeMethods.IsWindowVisible(hwnd))
                return true;
            var sb = new StringBuilder(64);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
            if (sb.ToString() == "MAME")
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Polls until the game window exists, the process dies, or the timeout passes.</summary>
    public static async Task<IntPtr> FindGameWindowAsync(
        int pid, TimeSpan timeout, Func<bool> processHasExited, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !processHasExited())
        {
            ct.ThrowIfCancellationRequested();
            var hwnd = FindGameWindow(pid);
            if (hwnd != IntPtr.Zero)
                return hwnd;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        return IntPtr.Zero;
    }

    public static PixelSize GetClientSize(IntPtr hwnd) =>
        NativeMethods.GetClientRect(hwnd, out var r)
            ? new PixelSize(r.Right, r.Bottom)
            : default;

    public static PixelRect GetWindowBounds(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var r)
            ? new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top)
            : default;
}
