using System.Text.Json;
using Attractor.Core.Configuration;

namespace Attractor.Core.Catalog;

/// <summary>Identity of a mame.exe; when it changes, every cache derived from it is stale.</summary>
public sealed record MameFingerprint(string ExePath, long Length, DateTime LastWriteUtc)
{
    public static MameFingerprint Of(string exePath)
    {
        var info = new FileInfo(exePath);
        return new MameFingerprint(Path.GetFullPath(exePath), info.Length, info.LastWriteTimeUtc);
    }
}

public sealed record MachinesCache(MameFingerprint Fingerprint, string? Build, List<MachineInfo> Machines);

public sealed record VerifyCache(MameFingerprint Fingerprint, DateTime VerifiedUtc, Dictionary<string, VerifyResult> Results);

/// <summary>JSON load/save with fingerprint validation, shared by both caches.</summary>
public static class CacheStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static T? Load<T>(string path, MameFingerprint expected) where T : class
    {
        if (!File.Exists(path))
            return null;
        try
        {
            var cache = JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
            var fingerprint = cache switch
            {
                MachinesCache m => m.Fingerprint,
                VerifyCache v => v.Fingerprint,
                _ => null,
            };
            return fingerprint == expected ? cache : null;
        }
        catch (JsonException)
        {
            return null; // corrupt cache = no cache
        }
    }

    public static void Save<T>(string path, T cache) =>
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(cache, Options));
}
