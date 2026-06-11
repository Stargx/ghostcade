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
        public CancellationTokenSource Cts { get; } = new(TimeSpan.FromSeconds(30));
        public Task? LoopTask { get; private set; }

        public Harness(string[] pool, RotationOptions? options = null)
        {
            Engine = new RotationEngine(
                Launcher, () => pool, Banned, @"c:\fake\mame.exe",
                options ?? FastOptions, new FakeWindowFinder(), random: new Random(42));
            Engine.GameChanged += g => { lock (GamesSeen) GamesSeen.Add(g); };
            Engine.GameFaulted += f => { lock (Faults) Faults.Add(f); };
        }

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
    public async Task Crashing_game_faults_then_quarantines_then_engine_faults_on_empty_pool()
    {
        await using var h = new Harness(["only"]);
        h.Launcher.OnLaunch = (_, proc) => proc.Exit(-5); // crash instantly, every time
        h.Start();
        await Wait.ForAsync(() => h.Engine.State == RotationState.Faulted, 15000, "engine faulted");
        lock (h.Faults)
        {
            Assert.Equal(FaultVerdict.SkipGame, h.Faults[0].Verdict);
            Assert.Equal(FaultVerdict.QuarantineGame, h.Faults[1].Verdict);
            Assert.All(h.Faults, f => Assert.Equal(GameFaultKind.CrashedAtLaunch, f.Kind));
        }
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
