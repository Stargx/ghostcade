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
        public FakeWindowFinder Finder { get; } = new();
        public InMemoryTagStore Banned { get; } = new();
        public RotationEngine Engine { get; }
        public List<string> GamesSeen { get; } = [];
        public List<GameFault> Faults { get; } = [];
        public List<PlaySession> PlaySessions { get; } = [];
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
                options ?? FastOptions, Finder, random: new Random(42),
                onBagChanged: s => { lock (BagSnapshots) BagSnapshots.Add(s); },
                filteredOut: filteredOut);
            Engine.GameChanged += c => { lock (GamesSeen) GamesSeen.Add(c.Game); };
            Engine.GameFaulted += f => { lock (Faults) Faults.Add(f); };
            Engine.PlaySessionChanged += p => { lock (PlaySessions) PlaySessions.Add(p); };
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

    [Fact]
    public async Task Hung_game_is_killed_by_the_watchdog_and_faulted()
    {
        // Watchdog = chunk*0 + 1s of wall clock; the game runs forever (SMB stall,
        // slow-clock driver). It must be killed and recorded as Hung, not waited out.
        await using var h = new Harness(["only"],
            FastOptions with { WatchdogBaseSeconds = 1 });
        h.Launcher.OnLaunch = (_, _) => { }; // never exits on its own
        h.Start();
        await Wait.ForAsync(() => { lock (h.Faults) return h.Faults.Count >= 1; }, 15000, "watchdog fault");
        lock (h.Faults)
            Assert.Equal(GameFaultKind.Hung, h.Faults[0].Kind);
        Assert.True(h.Launcher.Launches[0].Proc.HasExited, "watchdog must kill the hung game");
    }

    [Fact]
    public async Task Windowless_game_is_killed_and_faulted_as_NoWindow()
    {
        await using var h = new Harness(["only"]);
        h.Finder.FindWindows = false;        // MAME runs but never opens a window
        h.Launcher.OnLaunch = (_, _) => { }; // alive until killed
        h.Start();
        await Wait.ForAsync(() => { lock (h.Faults) return h.Faults.Count >= 1; }, 15000, "NoWindow fault");
        lock (h.Faults)
            Assert.Equal(GameFaultKind.NoWindow, h.Faults[0].Kind);
        Assert.True(h.Launcher.Launches[0].Proc.HasExited, "a windowless game must not be left running");
    }

    [Fact]
    public async Task Nonzero_exit_after_the_crash_window_counts_as_a_completed_chunk()
    {
        // Real MAME exits nonzero for some games' natural chunk end paths; only a FAST
        // nonzero exit is a crash. With the window at 0 every exit is "after" it.
        await using var h = new Harness(["a", "b"], FastOptions with { CrashWindowSeconds = 0 });
        h.Launcher.OnLaunch = (_, proc) => Task.Delay(30).ContinueWith(_ => proc.Exit(-5));
        h.Start();
        await h.SeenAtLeast(2); // advances normally
        lock (h.Faults)
            Assert.Empty(h.Faults);
    }

    [Fact]
    public async Task Launcher_throw_faults_the_game_and_escalates_to_engine_fault()
    {
        // CreateProcessW failing (moved exe, dead share) must escalate through
        // FaultPolicy to the Faulted state — never silently end the loop as Stopped.
        await using var h = new Harness(["a", "b", "c", "d", "e", "f"]);
        h.Launcher.OnLaunch = (_, _) => throw new System.ComponentModel.Win32Exception(53 /* BAD_NETPATH */);
        h.Start();
        await Wait.ForAsync(() => h.Engine.State == RotationState.Faulted, 15000, "engine faulted");
        lock (h.Faults)
        {
            Assert.All(h.Faults, f => Assert.Equal(GameFaultKind.LaunchFailed, f.Kind));
            Assert.Contains(h.Faults, f => f.Verdict == FaultVerdict.EngineFaulted);
        }
    }

    [Fact]
    public async Task Ban_while_idle_lands_on_the_game_it_was_aimed_at()
    {
        // A ban is aimed at what the user is looking at; even landing after the pool
        // emptied (or between games) it must ban THAT game, not whatever comes next.
        await using var h = new Harness(["a"]);
        h.Launcher.OnLaunch = (_, _) => { };
        h.Start();
        await h.SeenAtLeast(1);
        lock (h.Pool) h.Pool.Clear();
        h.Engine.Skip(); // draw against the now-empty pool -> idle
        await Wait.ForAsync(() => h.Engine.State == RotationState.Empty, 15000, "idle on empty pool");

        h.Engine.Ban(); // aimed at "a", the last game shown
        await Wait.ForAsync(() => h.Banned.Contains("a"), 15000, "ban landed while idle");
    }

    [Fact]
    public async Task Play_launches_an_uncapped_play_session_then_restarts_the_same_game()
    {
        await using var h = new Harness(["a", "b"]);
        h.Launcher.OnLaunch = (spec, proc) =>
        {
            if (spec.PlayMode)
                Task.Delay(80).ContinueWith(_ => proc.Exit(0)); // the user plays, then quits
            // attract chunks run until killed
        };
        h.Start();
        await h.SeenAtLeast(1);
        string playing = h.Engine.CurrentGame!;

        h.Engine.PlayCurrent();
        await Wait.ForAsync(
            () => { lock (h.PlaySessions) return h.PlaySessions.Count >= 2; }, 15000, "play session start+end");
        lock (h.PlaySessions)
        {
            Assert.Equal([true, false], h.PlaySessions.Take(2).Select(p => p.Active));
            Assert.All(h.PlaySessions, p => Assert.Equal(playing, p.Game));
        }

        Assert.True(h.Launcher.Launches[0].Proc.HasExited, "play must kill the attract chunk");
        var play = h.Launcher.Launches[1];
        Assert.True(play.Spec.PlayMode, "play session launches in play mode");
        Assert.Equal(0, play.Spec.SecondsToRun); // no 300s cap — a human is at the controls

        // the rotation resumes with the same game, in attract (non-play) mode
        await h.SeenAtLeast(2);
        lock (h.GamesSeen)
            Assert.Equal(playing, h.GamesSeen[1]);
        await Wait.ForAsync(() => { lock (h.Launcher.Launches) return h.Launcher.Launches.Count >= 3; },
            15000, "attract relaunch after play");
        lock (h.Launcher.Launches)
        {
            Assert.Equal(playing, h.Launcher.Launches[2].Spec.GameName);
            Assert.False(h.Launcher.Launches[2].Spec.PlayMode);
        }
    }

    [Fact]
    public async Task Stop_during_a_play_session_kills_it_and_ends_the_loop()
    {
        await using var h = new Harness(["a"]);
        h.Launcher.OnLaunch = (_, _) => { }; // both attract and play run until killed
        h.Start();
        await h.SeenAtLeast(1);
        h.Engine.PlayCurrent();
        await Wait.ForAsync(
            () => { lock (h.PlaySessions) return h.PlaySessions.Count >= 1; }, 15000, "play session active");

        await h.Engine.StopAsync();

        Assert.Equal(RotationState.Stopped, h.Engine.State);
        lock (h.Launcher.Launches)
            Assert.True(h.Launcher.Launches[^1].Proc.HasExited, "stop must kill the play session");
        lock (h.PlaySessions)
            Assert.False(h.PlaySessions[^1].Active); // the end event still fired
    }

    [Fact]
    public async Task Skip_during_a_play_session_is_swallowed()
    {
        // Rotation commands make no sense while the user holds the controls; a stray
        // global hotkey must not yank the game away mid-credit.
        await using var h = new Harness(["a", "b"]);
        h.Launcher.OnLaunch = (_, _) => { };
        h.Start();
        await h.SeenAtLeast(1);
        h.Engine.PlayCurrent();
        await Wait.ForAsync(
            () => { lock (h.PlaySessions) return h.PlaySessions.Count >= 1; }, 15000, "play session active");

        h.Engine.Skip();
        await Task.Delay(300); // give a (wrongly honored) skip time to kill the session

        lock (h.PlaySessions)
            Assert.Single(h.PlaySessions); // still active — no end event
        lock (h.Launcher.Launches)
            Assert.False(h.Launcher.Launches[^1].Proc.HasExited, "play session must survive a Skip");
    }
}
