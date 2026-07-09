namespace Attractor.Core.Windowing;

/// <summary>
/// Fallback embedder (config: "window.embedMode": "reparent"). True SetParent
/// child embedding. Carries the classic cross-process risks (input-queue
/// attachment, focus quirks with DirectInput-era MAME) — shipped dormant as
/// an escape hatch for MAME builds whose top-level window misbehaves.
/// </summary>
public sealed class ReparentEmbedder : IMameWindowEmbedder
{
    private IntPtr _mame;
    private PixelSize _native;

    public void Embed(IntPtr mameHwnd, IntPtr ownerHwnd, PixelRect hostRect, PixelSize nativeClientSize)
    {
        _mame = mameHwnd;
        _native = nativeClientSize;

        long style = NativeMethods.GetWindowLongPtr(mameHwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME | NativeMethods.WS_SYSMENU |
                   NativeMethods.WS_POPUP);
        style |= NativeMethods.WS_CHILD;
        NativeMethods.SetWindowLongPtr(mameHwnd, NativeMethods.GWL_STYLE, style);

        NativeMethods.SetParent(mameHwnd, ownerHwnd);
        UpdateBounds(hostRect);

        // Launched hidden (so MAME's init focus grab had no visible target); reveal the
        // now-parented, positioned child without activating it.
        MameWindowLocator.RevealNoActivate(mameHwnd);
    }

    public void UpdateBounds(PixelRect hostRect)
    {
        if (_mame == IntPtr.Zero || !NativeMethods.IsWindow(_mame))
            return;
        var fit = PixelRect.FitAspect(_native, hostRect);
        NativeMethods.SetWindowPos(_mame, IntPtr.Zero, fit.X, fit.Y, fit.Width, fit.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    public void Release()
    {
        if (_mame != IntPtr.Zero && NativeMethods.IsWindow(_mame))
            NativeMethods.SetParent(_mame, IntPtr.Zero);
        _mame = IntPtr.Zero;
    }
}
