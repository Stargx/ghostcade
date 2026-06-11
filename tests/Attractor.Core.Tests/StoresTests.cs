using Attractor.Core.Catalog;
using Attractor.Core.Configuration;

namespace Attractor.Core.Tests;

public class StoresTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("attractor-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void TagStore_roundtrips_and_toggles()
    {
        var path = PathOf("banned.txt");
        var store = new FileTagStore(path);
        Assert.True(store.Add("galaga"));
        Assert.False(store.Add("galaga"));
        Assert.True(store.Toggle("1942"));
        Assert.False(store.Toggle("1942"));

        var reloaded = new FileTagStore(path);
        Assert.True(reloaded.Contains("galaga"));
        Assert.False(reloaded.Contains("1942"));
    }

    [Fact]
    public void Config_defaults_when_missing_and_roundtrips()
    {
        var path = PathOf("config.json");
        var fresh = ConfigStore.Load(path);
        Assert.Equal(300, fresh.Rotation.DwellSeconds);

        ConfigStore.Save(path, fresh with { Mame = new AppConfig.MameSection { ExePath = @"c:\m\mame.exe" } });
        Assert.Equal(@"c:\m\mame.exe", ConfigStore.Load(path).Mame.ExePath);
    }

    [Fact]
    public void Config_tolerates_comments_and_refuses_future_versions()
    {
        var path = PathOf("config.json");
        File.WriteAllText(path, """
            {
              // hand-edited
              "version": 1,
              "rotation": { "dwellSeconds": 420, },
            }
            """);
        Assert.Equal(420, ConfigStore.Load(path).Rotation.DwellSeconds);

        File.WriteAllText(path, """{ "version": 99 }""");
        Assert.Throws<InvalidDataException>(() => ConfigStore.Load(path));
    }

    [Fact]
    public void Caches_invalidate_on_fingerprint_change_and_corruption()
    {
        var mamePath = PathOf("mame.exe");
        File.WriteAllText(mamePath, "fake mame");
        var fp = MameFingerprint.Of(mamePath);

        var cachePath = PathOf("machines-cache.json");
        CacheStore.Save(cachePath, new MachinesCache(fp, "0.147", [
            new MachineInfo("galaga", "Galaga", "1981", "Namco", false, false, true, null, DriverStatus.Good, 90)
        ]));

        Assert.NotNull(CacheStore.Load<MachinesCache>(cachePath, fp));

        File.WriteAllText(mamePath, "fake mame but bigger now");
        Assert.Null(CacheStore.Load<MachinesCache>(cachePath, MameFingerprint.Of(mamePath)));

        File.WriteAllText(cachePath, "{ not json");
        Assert.Null(CacheStore.Load<MachinesCache>(cachePath, fp));
    }

    [Fact]
    public void Database_assembly_filters_like_the_prototype()
    {
        MachineInfo[] machines =
        [
            new("galaga", "Galaga", "1981", "Namco", false, false, true, null, DriverStatus.Good, 90),
            new("kov", "Knights of Valour", "1999", "IGS", false, false, true, null, DriverStatus.Preliminary, 0),
            new("neogeo", "Neo-Geo", "1990", "SNK", true, false, true, null, DriverStatus.Good, 0),
            new("ym2151", "YM2151", null, null, false, true, false, null, DriverStatus.Unknown, 0),
            new("unverified", "No Roms For This", "1985", "x", false, false, true, null, DriverStatus.Good, 0),
        ];
        var verified = new Dictionary<string, VerifyResult>
        {
            ["galaga"] = VerifyResult.Good,
            ["kov"] = VerifyResult.Good,
            ["neogeo"] = VerifyResult.Good,
        };

        var db = GameDatabase.Assemble(machines, verified, new InMemoryTagStore(), new InMemoryTagStore());

        Assert.Equal(["galaga"], db.All.Select(e => e.Name));
        Assert.True(db.Find("galaga")!.IsVertical);

        db.Banned.Add("galaga");
        Assert.Empty(db.RotationPool());
    }
}
