namespace Attractor.Core.Windowing;

/// <summary>A rectangle in physical screen pixels (never WPF DIPs).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    /// <summary>
    /// Largest aspect-preserving fit of <paramref name="content"/> centered
    /// inside <paramref name="host"/> (letterboxes vertical games).
    /// </summary>
    public static PixelRect FitAspect(PixelSize content, PixelRect host)
    {
        if (content.Width <= 0 || content.Height <= 0 || host.Width <= 0 || host.Height <= 0)
            return host;

        double scale = Math.Min((double)host.Width / content.Width, (double)host.Height / content.Height);
        int w = Math.Max(1, (int)Math.Round(content.Width * scale));
        int h = Math.Max(1, (int)Math.Round(content.Height * scale));
        return new PixelRect(host.X + (host.Width - w) / 2, host.Y + (host.Height - h) / 2, w, h);
    }
}

/// <summary>A size in physical screen pixels.</summary>
public readonly record struct PixelSize(int Width, int Height);
