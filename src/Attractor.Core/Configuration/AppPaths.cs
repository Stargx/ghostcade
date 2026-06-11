namespace Attractor.Core.Configuration;

/// <summary>%APPDATA%\Attractor\* file layout.</summary>
public sealed class AppPaths
{
    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Attractor");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(LogsDir);
    }

    public string Root { get; }
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string MachinesCacheFile => Path.Combine(Root, "machines-cache.json");
    public string VerifyCacheFile => Path.Combine(Root, "verify-cache.json");
    public string BannedFile => Path.Combine(Root, "banned.txt");
    public string FavoritesFile => Path.Combine(Root, "favorites.txt");
    public string PlacementFile => Path.Combine(Root, "placement.json");
    public string LogsDir => Path.Combine(Root, "logs");
}
