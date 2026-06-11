namespace Attractor.Core.Windowing;

/// <summary>
/// Makes a live MAME window read as part of the app. Embed is idempotent and
/// called once per chunk relaunch (a fresh MAME window every ≤299s).
/// </summary>
public interface IMameWindowEmbedder
{
    /// <param name="mameHwnd">The freshly located MAME game window.</param>
    /// <param name="ownerHwnd">The app's main window handle.</param>
    /// <param name="hostRect">Target region in physical screen pixels.</param>
    /// <param name="nativeClientSize">MAME's 1x client size (sets the aspect ratio).</param>
    void Embed(IntPtr mameHwnd, IntPtr ownerHwnd, PixelRect hostRect, PixelSize nativeClientSize);

    /// <summary>Re-fit after the app window moved or the layout changed.</summary>
    void UpdateBounds(PixelRect hostRect);

    /// <summary>Forget the current window (it is about to die or be replaced).</summary>
    void Release();
}
