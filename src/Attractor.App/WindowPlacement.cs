using System.IO;
using System.Text.Json;
using System.Windows;
using Attractor.Core.Configuration;
using Attractor.Core.Diagnostics;

namespace Attractor.App;

public sealed record PlacementData(double Left, double Top, double Width, double Height, bool IsMaximized);

/// <summary>Persists the app window's bounds across sessions (placement.json).</summary>
public static class WindowPlacement
{
    public static void Restore(Window window, string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var p = JsonSerializer.Deserialize<PlacementData>(File.ReadAllText(path));
            if (p is null)
                return;
            // hand-edited/corrupt sizes: Rect throws on negatives, and !(x > 0) also rejects NaN
            if (!(p.Width > 0) || !(p.Height > 0))
                return;

            // only apply if the saved rect still intersects the virtual desktop
            var virtualRect = new Rect(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var saved = new Rect(p.Left, p.Top, p.Width, p.Height);
            if (!virtualRect.IntersectsWith(saved))
                return;

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = p.Left;
            window.Top = p.Top;
            window.Width = p.Width;
            window.Height = p.Height;
            if (p.IsMaximized)
                window.WindowState = WindowState.Maximized;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable placement file: fall back to defaults. Letting an
            // IOException out would abort the MainWindow ctor and leave a windowless
            // process holding the single-instance mutex.
        }
    }

    /// <summary>Best-effort, like every other persisted-state write (the data folder may
    /// be a locked/offline network share). This runs inside OnClosing after the close has
    /// been deferred — a throw here would leave the window permanently unclosable.</summary>
    public static void Save(Window window, string path)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var data = new PlacementData(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            window.WindowState == WindowState.Maximized);
        try { AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.Log.Warn("couldn't save window placement", ex);
        }
    }
}
