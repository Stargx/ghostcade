using Attractor.Core.Mame;

namespace Attractor.Core.Catalog;

public sealed record ScanProgress(string Stage, int Count);

/// <summary>
/// Builds (or loads from cache) everything derived from a MAME install:
/// the machine list (-listxml) and the verified-romset map (-verifyroms).
/// Warm boots hit both caches and spawn no MAME processes at all.
/// </summary>
public static class CatalogBuilder
{
    public static async Task<GameDatabase> BuildAsync(
        string mameExePath,
        string machinesCachePath,
        string verifyCachePath,
        ITagStore banned,
        ITagStore favorites,
        IProgress<ScanProgress>? progress = null,
        bool forceRescan = false,
        CancellationToken ct = default)
    {
        var fingerprint = MameFingerprint.Of(mameExePath);

        var machinesCache = forceRescan ? null : CacheStore.Load<MachinesCache>(machinesCachePath, fingerprint);
        if (machinesCache is null)
        {
            var machines = new List<MachineInfo>();
            using (var proc = MameVerbRunner.Start(mameExePath, "-listxml"))
            {
                var drainErr = proc.StandardError.ReadToEndAsync(ct);
                await foreach (var machine in MameListXmlParser.ParseAsync(proc.StandardOutput.BaseStream, ct))
                {
                    machines.Add(machine);
                    if (machines.Count % 500 == 0)
                        progress?.Report(new ScanProgress("listxml", machines.Count));
                }
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                _ = await drainErr.ConfigureAwait(false);
            }
            machinesCache = new MachinesCache(fingerprint, Build: null, machines);
            CacheStore.Save(machinesCachePath, machinesCache);
        }
        progress?.Report(new ScanProgress("listxml", machinesCache.Machines.Count));

        var verifyCache = forceRescan ? null : CacheStore.Load<VerifyCache>(verifyCachePath, fingerprint);
        if (verifyCache is null)
        {
            var results = await RomVerifier.VerifyAsync(
                mameExePath,
                new Progress<int>(n => progress?.Report(new ScanProgress("verify", n))),
                ct).ConfigureAwait(false);
            verifyCache = new VerifyCache(fingerprint, DateTime.UtcNow, results);
            CacheStore.Save(verifyCachePath, verifyCache);
        }
        progress?.Report(new ScanProgress("verify", verifyCache.Results.Count));

        return GameDatabase.Assemble(machinesCache.Machines, verifyCache.Results, banned, favorites);
    }
}
