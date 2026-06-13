using System.Diagnostics;
using System.Threading.Channels;
using Attractor.Core.Catalog;
using Attractor.Core.Mame;

namespace Attractor.Core.Rotation;

/// <summary>
/// The rotation state machine. One async loop owns the MAME child process;
/// the UI talks to it only through commands (thread-safe) and events (raised
/// on worker threads — hosts must marshal to their UI thread).
/// </summary>
public sealed class RotationEngine : IAsyncDisposable
{
    private readonly IMameLauncher _launcher;
    private readonly IGameWindowFinder _finder;
    private readonly Func<IReadOnlyList<string>> _poolProvider;
    private readonly ITagStore _banned;
    private readonly RotationOptions _options;
    private readonly string _mameExePath;
    private readonly IReadOnlyList<string>? _extraArgs;
    private readonly TimeProvider _time;

    private readonly Channel<EngineCommand> _commands = Channel.CreateUnbounded<EngineCommand>();
    private readonly ShuffleBag _bag;
    private readonly PlayHistory _history = new();
    private readonly FaultPolicy _faults;

    private IMameProcess? _proc;
    private Task? _loopTask;
    private Navigation _pendingNav = Navigation.Forward;

    private enum Navigation { Forward, Back, RestartCurrent }
    private enum ChunkResult { Completed, CommandAdvance, CommandStop, Faulted }
    private enum GameOutcome { Advance, Stop, EngineFault }

    public RotationEngine(
        IMameLauncher launcher,
        Func<IReadOnlyList<string>> poolProvider,
        ITagStore banned,
        string mameExePath,
        RotationOptions? options = null,
        IGameWindowFinder? finder = null,
        TimeProvider? time = null,
        Random? random = null,
        IReadOnlyList<string>? extraArgs = null,
        IEnumerable<string>? savedBagQueue = null,
        Action<IReadOnlyList<string>>? onBagChanged = null)
    {
        _launcher = launcher;
        _poolProvider = poolProvider;
        _banned = banned;
        _mameExePath = mameExePath;
        _options = options ?? new RotationOptions();
        _finder = finder ?? new GameWindowFinder();
        _time = time ?? TimeProvider.System;
        _extraArgs = extraArgs;
        _onBagChanged = onBagChanged;
        _bag = new ShuffleBag(poolProvider, random, savedBagQueue);
        _faults = new FaultPolicy(_time);
    }

    private readonly Action<IReadOnlyList<string>>? _onBagChanged;

    public RotationState State { get; private set; } = RotationState.Stopped;
    public string? CurrentGame { get; private set; }
    public bool IsHeld { get; private set; }

    public event Action<string>? GameChanged;
    public event Action<MameWindowReady>? WindowReady;
    public event Action<RotationState>? StateChanged;
    public event Action<GameFault>? GameFaulted;
    public event Action<bool>? HoldChanged;

    // ---- commands (thread-safe, callable from UI/hotkeys) ----------------------

    public void Skip() => _commands.Writer.TryWrite(EngineCommand.Skip);
    public void Previous() => _commands.Writer.TryWrite(EngineCommand.Previous);
    public void ToggleHold() => _commands.Writer.TryWrite(EngineCommand.ToggleHold);
    public void Ban() => _commands.Writer.TryWrite(EngineCommand.Ban);

    public Task StartAsync(CancellationToken shutdown)
    {
        if (_loopTask is not null)
            throw new InvalidOperationException("engine already started");
        _loopTask = Task.Run(() => RunLoopAsync(shutdown), CancellationToken.None);
        return _loopTask;
    }

    public async Task StopAsync()
    {
        _commands.Writer.TryWrite(EngineCommand.Stop);
        if (_loopTask is not null)
            await _loopTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopAsync().ConfigureAwait(false); }
        catch { /* engine is going away regardless */ }
    }

    // ---- loop -------------------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken shutdown)
    {
        SetState(RotationState.Running);
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                var game = NextGame();
                if (game is null)
                {
                    SetState(RotationState.Faulted);
                    return;
                }

                CurrentGame = game;
                GameChanged?.Invoke(game);

                var outcome = await PlayGameAsync(game, shutdown).ConfigureAwait(false);
                if (outcome == GameOutcome.Stop)
                    return;
                if (outcome == GameOutcome.EngineFault)
                {
                    SetState(RotationState.Faulted);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // app shutdown
        }
        finally
        {
            _proc?.Kill();
            _proc?.Dispose();
            if (State != RotationState.Faulted)
                SetState(RotationState.Stopped);
        }
    }

    private bool Excluded(string game) => _banned.Contains(game) || _faults.IsQuarantined(game);

    private string? NextGame()
    {
        switch (_pendingNav)
        {
            case Navigation.Back:
                _pendingNav = Navigation.Forward;
                var back = _history.TryBack(Excluded);
                if (back is not null)
                    return back;
                goto case Navigation.RestartCurrent;

            case Navigation.RestartCurrent:
                _pendingNav = Navigation.Forward;
                if (CurrentGame is { } current && !Excluded(current))
                    return current;
                goto default;

            default:
                var replay = _history.TryForward(Excluded);
                if (replay is not null)
                    return replay;
                var fresh = _bag.Draw(Excluded);
                if (fresh is not null)
                {
                    _history.Append(fresh);
                    _onBagChanged?.Invoke(_bag.Snapshot()); // persist cycle progress
                }
                return fresh;
        }
    }

    private async Task<GameOutcome> PlayGameAsync(string game, CancellationToken ct)
    {
        var chunks = ChunkPlanner.Plan(_options.DwellSeconds);
        int index = 0;
        while (index < chunks.Count || IsHeld)
        {
            int chunkSeconds = index < chunks.Count ? chunks[index] : ChunkPlanner.MaxChunkSeconds;
            var result = await RunChunkAsync(game, chunkSeconds, ct).ConfigureAwait(false);
            switch (result)
            {
                case ChunkResult.Completed:
                    _faults.RecordSuccess(game);
                    index++;
                    break;
                case ChunkResult.CommandAdvance:
                    return GameOutcome.Advance;
                case ChunkResult.CommandStop:
                    return GameOutcome.Stop;
                case ChunkResult.Faulted:
                    return _lastVerdict == FaultVerdict.EngineFaulted ? GameOutcome.EngineFault : GameOutcome.Advance;
            }
        }
        return GameOutcome.Advance;
    }

    private FaultVerdict _lastVerdict;

    private ChunkResult Fault(string game, GameFaultKind kind, int? exitCode)
    {
        _lastVerdict = _faults.RecordFault(game);
        GameFaulted?.Invoke(new GameFault(game, kind, _lastVerdict, exitCode));
        return ChunkResult.Faulted;
    }

    private async Task<ChunkResult> RunChunkAsync(string game, int chunkSeconds, CancellationToken ct)
    {
        _proc?.Dispose();
        _proc = _launcher.Launch(new MameLaunchSpec(_mameExePath, game, chunkSeconds, _extraArgs));
        var proc = _proc;
        var started = Stopwatch.StartNew();

        var hwnd = await _finder.FindAsync(
            proc.Pid, TimeSpan.FromSeconds(_options.WindowTimeoutSeconds), () => proc.HasExited, ct)
            .ConfigureAwait(false);

        if (hwnd == IntPtr.Zero)
        {
            if (proc.HasExited)
                return Fault(game, GameFaultKind.CrashedAtLaunch, proc.ExitCode);
            proc.Kill();
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return Fault(game, GameFaultKind.NoWindow, null);
        }

        if (_options.SettleMs > 0)
            await Task.Delay(TimeSpan.FromMilliseconds(_options.SettleMs), _time, ct).ConfigureAwait(false);
        WindowReady?.Invoke(new MameWindowReady(game, proc.Pid, hwnd, _finder.GetClientSize(hwnd), chunkSeconds));

        var exitTask = proc.WaitForExitAsync(ct);
        var watchdog = Task.Delay(WatchdogFor(chunkSeconds), _time, ct);

        while (true)
        {
            var commandReady = _commands.Reader.WaitToReadAsync(ct).AsTask();
            var winner = await Task.WhenAny(exitTask, watchdog, commandReady).ConfigureAwait(false);

            if (winner == exitTask)
            {
                bool crashed = proc.ExitCode is { } code && code != 0 &&
                               started.Elapsed < TimeSpan.FromSeconds(_options.CrashWindowSeconds);
                return crashed
                    ? Fault(game, GameFaultKind.CrashedAtLaunch, proc.ExitCode)
                    : ChunkResult.Completed;
            }

            if (winner == watchdog)
            {
                proc.Kill();
                await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                return Fault(game, GameFaultKind.Hung, null);
            }

            // command available
            if (!_commands.Reader.TryRead(out var command))
                continue;
            switch (command)
            {
                case EngineCommand.ToggleHold:
                    IsHeld = !IsHeld;
                    HoldChanged?.Invoke(IsHeld);
                    continue; // game keeps running

                case EngineCommand.Skip:
                    _pendingNav = Navigation.Forward;
                    return await KillAndAdvance();

                case EngineCommand.Previous:
                    _pendingNav = Navigation.Back;
                    return await KillAndAdvance();

                case EngineCommand.Ban:
                    _banned.Add(game);
                    _pendingNav = Navigation.Forward;
                    return await KillAndAdvance();

                case EngineCommand.Stop:
                    proc.Kill();
                    await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    return ChunkResult.CommandStop;
            }
        }

        async Task<ChunkResult> KillAndAdvance()
        {
            proc.Kill();
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return ChunkResult.CommandAdvance;
        }
    }

    private TimeSpan WatchdogFor(int chunkSeconds) =>
        TimeSpan.FromSeconds(chunkSeconds * _options.WatchdogFactor + _options.WatchdogBaseSeconds);

    private void SetState(RotationState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }
}
