namespace Attractor.Core.Windowing;

/// <summary>
/// Default embedder. MAME stays a TOP-LEVEL window — no SetParent, so a MAME
/// stalled on a slow network ROM load can never freeze the app's input queue.
/// Instead the window is made chromeless and non-activatable, then OWNED by
/// the app window (GWL_HWNDPARENT): owned windows ride their owner's z-order,
/// rise with it, and hide on minimize. Continuous UpdateBounds calls keep it
/// glued over the host region.
/// </summary>
public sealed class GlueEmbedder : IMameWindowEmbedder
{
    private IntPtr _mame;
    private PixelSize _native;
    private PixelRect _lastHost;

    public void Embed(IntPtr mameHwnd, IntPtr ownerHwnd, PixelRect hostRect, PixelSize nativeClientSize)
    {
        _mame = mameHwnd;
        _native = nativeClientSize;

        long style = NativeMethods.GetWindowLongPtr(mameHwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU |
                   NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX);
        NativeMethods.SetWindowLongPtr(mameHwnd, NativeMethods.GWL_STYLE, style);

        long exStyle = NativeMethods.GetWindowLongPtr(mameHwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(mameHwnd, NativeMethods.GWL_EXSTYLE, exStyle);

        NativeMethods.SetWindowLongPtr(mameHwnd, NativeMethods.GWL_HWNDPARENT, ownerHwnd.ToInt64());

        // apply the style change, then place it
        NativeMethods.SetWindowPos(mameHwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
        UpdateBounds(hostRect);

        // The window was launched hidden so MAME's init-time focus grab had no visible
        // target to steal. Now that it's chromeless, non-activating, owned and positioned,
        // reveal it without activating — no focus was ever taken from the user's foreground.
        MameWindowLocator.RevealNoActivate(mameHwnd);
    }

    public void UpdateBounds(PixelRect hostRect)
    {
        _lastHost = hostRect;
        if (_mame == IntPtr.Zero || !NativeMethods.IsWindow(_mame))
            return;

        // styles are stripped, so window rect == client rect: fit is exact
        var fit = PixelRect.FitAspect(_native, hostRect);
        NativeMethods.SetWindowPos(_mame, IntPtr.Zero, fit.X, fit.Y, fit.Width, fit.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    public PixelRect LastFit => PixelRect.FitAspect(_native, _lastHost);

    public void Release() => _mame = IntPtr.Zero;
}
