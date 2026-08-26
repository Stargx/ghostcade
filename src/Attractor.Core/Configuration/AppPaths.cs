namespace Attractor.Core.Configuration;

/// <summary>
/// Where Ghostcade keeps its config, caches, tags and logs.
///
/// By preference everything lives in a <c>data\</c> folder right next to the
/// executable — portable, and no hunting through %APPDATA%. If that location
/// is not writable (e.g. a machine-wide install under Program Files), it falls
/// back to %APPDATA%\Attractor. On first portable run, any existing
/// %APPDATA%\Attractor data is migrated across so the one-time ROM scan and
/// settings are not lost.
/// </summary>
public sealed class AppPaths
{
    private static readonly string[] DataFileNames =
    [
        "config.json", "machines-cache.json", "verify-cache.json",
        "banned.txt", "favorites.txt", "placement.json",
    ];

    public AppPaths(string? rootOverride = null)
    {
        Root = rootOverride ?? ResolveRoot();
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
    public string RotationStateFile => Path.Combine(Root, "rotation-state.json");
    public string LogsDir => Path.Combine(Root, "logs");

    private static string LegacyRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Attractor");

    private static string ResolveRoot()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "data");
        if (TryEnsureWritable(portable))
        {
            MigrateLegacyData(portable);
            return portable;
        }
        return LegacyRoot; // install dir is read-only — fall back to %APPDATA%
    }

    private static bool TryEnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".writetest");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Copy settings/caches from a previous %APPDATA% install. Retries on every
    /// launch until a run completes with nothing skipped (a locked favorites.txt on the
    /// first attempt must not be dropped forever), then stamps a marker and stops. The
    /// per-file "destination missing" gate keeps retries from clobbering newer data.</summary>
    private static void MigrateLegacyData(string portable)
    {
        var legacy = LegacyRoot;
        if (string.Equals(Path.GetFullPath(legacy), Path.GetFullPath(portable), StringComparison.OrdinalIgnoreCase))
            return;
        var marker = Path.Combine(portable, ".migration-done");
        if (!Directory.Exists(legacy) || File.Exists(marker))
            return; // nothing to migrate, or a past migration finished cleanly

        bool complete = true;
        foreach (var name in DataFileNames)
        {
            var src = Path.Combine(legacy, name);
            var dst = Path.Combine(portable, name);
            try
            {
                if (File.Exists(src) && !File.Exists(dst))
                    File.Copy(src, dst);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false; // skip a locked file; retry next launch
            }
        }
        if (complete)
        {
            try { File.WriteAllText(marker, ""); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
