using Attractor.Core.Catalog;
using Attractor.Core.Rotation;

namespace Attractor.Core.Tests;

public class RotationEngineTests
{
    private static readonly RotationOptions FastOptions = new()
    {
        DwellSeconds = 1,           // one 1s chunk per game
        WindowTimeoutSeconds = 2,
        SettleMs = 0,
        CrashWindowSeconds = 10,
        WatchdogFactor = 0,
        WatchdogBaseSeconds = 30,   // never fires unless a test wants it to
    };

    private sealed class Harness : IAsyncDisposable
    {
        public FakeLauncher Launcher { get; } = new();
        public InMemoryTagStore Banned { get; } = new();
        public RotationEngine Engine { get; }
        public List<string> GamesSeen { get; } = [];
        public List<GameFault> Faults { get; } = [];
        public List<IReadOnlyList<string>> BagSnapshots { get; } = [];
        // The live pool; mutate under its own lock (the engine reads it on the loop thread).
        public List<string> Pool { get; }
        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(30));
        public Task? LoopTask { get; private set; }

        public Harness(string[] pool, RotationOptions? options = null, Func<string, bool>? filteredOut = null)
        {
            Pool = [.. pool];
            Engine = new RotationEngine(
                Launcher, PoolSnapshot, Banned, @"c:\fake\mame.exe",
                options ?? FastOptions, new FakeWindowFinder(), random: new Random(42),
                onBagChanged: s => { lock (BagSnapshots) BagSnapshots.Add(s); },
                filteredOut: filteredOut);
            Engine.GameChanged += c => { lock (GamesSeen) GamesSeen.Add(c.Game); };
            Engine.GameFaulted += f => { lock (Faults) Faults.Add(f); };
        }

        // Like the real GameDatabase.RotationPool(), hand back a fresh copy so a test can
        // mutate Pool concurrently with the engine without an enumeration race.
        private IReadOnlyList<string> PoolSnapshot() { lock (Pool) return Pool.ToArray(); }

        public void WidenPool(params string[] add) { lock (Pool) Pool.AddRange(add); }

        public Task BagPersistedAtLeast(int n) =>
            Wait.ForAsync(() => { lock (BagSnapshots) return BagSnapshots.Count >= n; }, 15000, $"{n} bag snapshots");

        public void Start() => LoopTask = Engine.StartAsync(Cts.Token);

        public Task SeenAtLeast(int n) =>
            Wait.ForAsync(() => { lock (GamesSeen) return GamesSeen.Count >= n; }, 15000, $"{n} games seen");

        public async ValueTask DisposeAsync()
        {
            await Engine.StopAsync();
            Cts.Dispose();
        }
    }

    [Fact]
    public async Task Completes_chunks_and_advances_through_games()
    {
        await using var h = new Harness(["a", "b", "c"]);
        h.Start();
        await h.SeenAtLeast(4); // > pool size proves the bag reshuffles
        lock (h.GamesSeen)
        {
            Assert.Equal(3, h.GamesSeen.Take(3).ToHashSet().Count); // full cycle, no repeats
        }
    }

    [Fact]
    public async Task Skip_kills_the_current_chunk_and_advances()
    {
        await using var h = new Harness(["a", "b"]);
        h.Launcher.OnLaunch = (_, _) => { }; // games run until killed
        h.Start();
        await h.SeenAtLeast(1);
        h.Engine.Skip();
        await h.SeenAtLeast(2);
        Assert.True(h.Launcher.Launches[0].Proc.HasExited, "skip must kill the running game");
    }

    [Fact]
    public async Task Previous_returns_to_the_prior_game()
    {
        await using var h = new Harness(["a", "b", "c"]);
        h.Launcher.OnLaunch = (_, _) => { };
        h.Start();
        await h.SeenAtLeast(1);
        h.Engine.Skip();
        await h.SeenAtLeast(2);
        h.Engine.Previous();
        await h.SeenAtLeast(3);
        lock (h.GamesSeen)
            Assert.Equal(h.GamesSeen[0], h.GamesSeen[2]);
    }

    [Fact]
    public async Task Hold_keeps_feeding_chunks_of_the_same_game()
    {
        await using var h = new Harness(["a", "b", "c"]);
        h.Engine.ToggleHold(); // queued before the first chunk's wait phase
        h.Start();
        await Wait.ForAsync(() => { lock (h.Launcher.Launches) return h.Launcher.Launches.Count >= 3; },
            15000, "3 chunk launches");
        lock (h.Launcher.Launches)
        {
            var games = h.Launcher.Launches.Take(3).Select(l => l.Spec.GameName).Distinct();
            Assert.Single(games); // same game relaunched while held
        }
        Assert.True(h.Engine.IsHeld);
    }

    [Fact]
    public async Task Ban_records_and_never_draws_the_game_again()
    {
        await using var h = new Harness(["a", "b"]);
        h.Launcher.OnLaunch = (_, _) => { };
        h.Start();
        await h.SeenAtLeast(1);
        string banned = h.Engine.CurrentGame!;
        h.Engine.Ban();
        await h.SeenAtLeast(2);
        h.Engine.Skip(); // force one more draw to prove the ban filters it
        await h.SeenAtLeast(3);
        Assert.True(h.Banned.Contains(banned));
        lock (h.GamesSeen)
            Assert.DoesNotContain(banned, h.GamesSeen.Skip(1));
    }

    [Fact]
    public async Task Filtered_out_games_are_never_drawn()
    {
        await using var h = new Harness(["a", "b", "c"], filteredOut: g => g == "b");
        h.Start();
        await h.SeenAtLeast(4); // a full cycle plus a reshuffle
        lock (h.GamesSeen)
            Assert.DoesNotContain("b", h.GamesSeen);
    }

    [Fact]
    public async Task Rebag_persists_a_fresh_snapshot_without_advancing_the_current_game()
    {
        // The original bug: a filter change that did NOT evict the current game (a widen)
        // never rewrote rotation-state. Rebag must persist a fresh cycle regardless of Skip.
        await using var h = new Harness(["a", "b", "c"]);
        h.Launcher.OnLaunch = (_, _) => { }; // current game runs until killed
        h.Start();
        await h.SeenAtLeast(1);
        await h.BagPersistedAtLeast(1); // the initial draw persisted once
        string current = h.Engine.CurrentGame!;
        int seenBefore, snapsBefore;
        lock (h.GamesSeen) seenBefore = h.GamesSeen.Count;
        lock (h.BagSnapshots) snapsBefore = h.BagSnapshots.Count;

        h.Engine.Rebag();

        await h.BagPersistedAtLeast(snapsBefore + 1);   // Rebag persisted a fresh cycle...
        Assert.Equal(current, h.Engine.CurrentGame);    // ...without changing the current game...
        lock (h.GamesSeen)
            Assert.Equal(seenBefore, h.GamesSeen.Count); // ...and without advancing (no GameChanged)
    }

    [Fact]
    public async Task Rebag_reshuffles_to_include_a_widened_pool()
    {
        // A widen must surface the newly-eligible games at once, not after the old queue drains.
        await using var h = new Harness(["a"]);
        h.Launcher.OnLaunch = (_, _) => { }; // "a" runs until killed
        h.Start();
        await h.SeenAtLeast(1);
        await h.BagPersistedAtLeast(1);

        h.WidenPool("b", "c");
        h.Engine.Rebag();

        await Wait.ForAsync(
            () => { lock (h.BagSnapshots) return h.BagSnapshots[^1].ToHashSet().SetEquals(["a", "b", "c"]); },
            15000, "rebagged snapshot reflects the widened pool");
    }

    [Fact]
    public async Task Crashing_game_quarantines_then_engine_idles_on_empty_pool()
    {
        await using var h = new Harness(["only"]);
        h.Launcher.OnLaunch = (_, proc) => proc.Exit(-5); // crash instantly, every time
        h.Start();
        // The only game faults twice -> quarantined -> nothing left to draw. That's a
        // recoverable idle, NOT the terminal Faulted state (which is for MAME/share death).
        await Wait.ForAsync(() => h.Engine.State == RotationState.Empty, 15000, "engine idle on empty pool");
        lock (h.Faults)
        {
            Assert.Equal(FaultVerdict.SkipGame, h.Faults[0].Verdict);
            Assert.Equal(FaultVerdict.QuarantineGame, h.Faults[1].Verdict);
            Assert.All(h.Faults, f => Assert.Equal(GameFaultKind.CrashedAtLaunch, f.Kind));
        }
    }

    [Fact]
    public async Task Empty_pool_idles_and_recovers_instead_of_faulting()
    {
        await using var h = new Harness(["a"]);
        h.Launcher.OnLaunch = (_, _) => { }; // game runs until killed
        h.Start();
        await h.SeenAtLeast(1);

        h.Engine.Ban(); // ban the only game -> nothing left to draw
        await Wait.ForAsync(() => h.Engine.State == RotationState.Empty, 15000, "idle on empty pool");

        h.Banned.Toggle("a"); // it's eligible again
        h.Engine.Nudge();     // wake the idle loop to re-draw
        await h.SeenAtLeast(2);
        Assert.Equal(RotationState.Running, h.Engine.State);
    }

    [Fact]
    public async Task Relaunch_storm_faults_the_engine_within_the_global_window()
    {
        await using var h = new Harness(["a", "b", "c", "d", "e", "f"]);
        h.Launcher.OnLaunch = (_, proc) => proc.Exit(-5);
        h.Start();
        await Wait.ForAsync(() => h.Engine.State == RotationState.Faulted, 15000, "engine faulted");
        lock (h.Faults)
            Assert.Contains(h.Faults, f => f.Verdict == FaultVerdict.EngineFaulted);
    }

    [Fact]
    public async Task Stop_ends_the_loop_and_kills_the_child()
    {
        await using var h = new Harness(["a"]);
        h.Launcher.OnLaunch = (_, _) => { };
        h.Start();
        await h.SeenAtLeast(1);
        await h.Engine.StopAsync();
        Assert.Equal(RotationState.Stopped, h.Engine.State);
        lock (h.Launcher.Launches)
            Assert.True(h.Launcher.Launches[^1].Proc.HasExited);
    }
}
