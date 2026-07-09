// Attractor smoke harness: drives Attractor.Core against a real MAME install
// with no UI. Doubles as the reference implementation for any future host
// (e.g. the Unreal 3D arcade).
//
//   Attractor.Smoke scan <mame.exe>                  build/refresh caches, print counts
//   Attractor.Smoke rotate <mame.exe> [games] [dwellSeconds]   live rotation, un-embedded
//   Attractor.Smoke jobtest <mame.exe>               verify kill-on-close job object

using Attractor.Core.Catalog;
using Attractor.Core.Configuration;
using Attractor.Core.Mame;
using Attractor.Core.Rotation;
using Attractor.Core.Windowing;

return args switch
{
    ["scan", var mame] => await ScanAsync(mame),
    ["rotate", var mame, .. var rest] => await RotateAsync(mame, rest),
    ["jobtest", var mame] => await JobTestAsync(mame),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("usage: Attractor.Smoke scan|rotate|jobtest <mame.exe> [...]");
    return 64;
}

static async Task<(GameDatabase db, AppPaths paths)> BuildDbAsync(string mame)
{
    var paths = new AppPaths();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var db = await CatalogBuilder.BuildAsync(
        mame, paths.MachinesCacheFile, paths.VerifyCacheFile,
        new FileTagStore(paths.BannedFile), new FileTagStore(paths.FavoritesFile),
        new Progress<ScanProgress>(p => Console.Write($"\r{p.Stage}: {p.Count}        ")));
    Console.WriteLine($"\rcatalog ready in {sw.Elapsed.TotalSeconds:n1}s                ");
    return (db, paths);
}

static async Task<int> ScanAsync(string mame)
{
    var (db, paths) = await BuildDbAsync(mame);
    Console.WriteLine($"rotation-eligible games: {db.All.Count}");
    Console.WriteLine($"  good drivers:      {db.All.Count(e => e.Driver == DriverStatus.Good)}");
    Console.WriteLine($"  imperfect drivers: {db.All.Count(e => e.Driver == DriverStatus.Imperfect)}");
    Console.WriteLine($"  vertical games:    {db.All.Count(e => e.IsVertical)}");
    Console.WriteLine($"caches: {paths.Root}");
    return db.All.Count > 0 ? 0 : 1;
}

static async Task<int> RotateAsync(string mame, string[] rest)
{
    int games = rest.Length > 0 ? int.Parse(rest[0]) : 3;
    int dwell = rest.Length > 1 ? int.Parse(rest[1]) : 20;

    var (db, _) = await BuildDbAsync(mame);
    if (db.All.Count == 0)
    {
        Console.WriteLine("no eligible games — run scan / check rompath");
        return 1;
    }

    using var launcher = new MameLauncher();
    await using var engine = new RotationEngine(
        launcher, db.RotationPool, db.Banned, mame,
        new RotationOptions { DwellSeconds = dwell });

    int seen = 0;
    var done = new TaskCompletionSource();
    engine.GameChanged += c =>
    {
        var entry = db.Find(c.Game);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] now showing: {entry?.Title ?? c.Game} ({entry?.Year}) [{c.Reason}]");
        if (Interlocked.Increment(ref seen) >= games + 1)
            done.TrySetResult();
    };
    engine.WindowReady += w =>
    {
        Console.WriteLine($"           window 0x{w.Hwnd:X} native {w.NativeClientSize.Width}x{w.NativeClientSize.Height}, chunk {w.ChunkSeconds}s");
        // Attract chunks launch hidden and are normally revealed by the host's embedder.
        // Smoke runs un-embedded, so reveal (non-activating) here to keep `rotate` watchable.
        MameWindowLocator.RevealNoActivate(w.Hwnd);
    };
    engine.GameFaulted += f =>
        Console.WriteLine($"           FAULT {f.Game}: {f.Kind} -> {f.Verdict}");

    using var cts = new CancellationTokenSource();
    var loop = engine.StartAsync(cts.Token);
    await done.Task;
    await engine.StopAsync();
    Console.WriteLine($"rotation smoke complete: {seen - 1} games shown");
    return 0;
}

static async Task<int> JobTestAsync(string mame)
{
    using var launcher = new MameLauncher();
    var proc = launcher.Launch(new MameLaunchSpec(mame, "", SecondsToRun: 0));
    Console.WriteLine($"PID={proc.Pid}");
    await Task.Delay(4000);
    System.Diagnostics.Process.GetCurrentProcess().Kill(); // hard kill: job object must reap MAME
    return 0; // unreachable
}
