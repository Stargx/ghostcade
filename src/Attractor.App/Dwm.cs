using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Attractor.App;

internal static class Dwm
{
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Dark titlebar to match the cabinet theme (Win10 1809+; harmless elsewhere).</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref on, sizeof(int));
    }
}
