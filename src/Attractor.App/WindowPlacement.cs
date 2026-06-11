using System.IO;
using System.Text.Json;
using System.Windows;
using Attractor.Core.Configuration;

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
        catch (JsonException)
        {
            // corrupt placement file: fall back to defaults
        }
    }

    public static void Save(Window window, string path)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.Width, window.Height)
            : window.RestoreBounds;
        var data = new PlacementData(
            bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            window.WindowState == WindowState.Maximized);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data));
    }
}
