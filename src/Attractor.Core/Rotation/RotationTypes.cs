using Attractor.Core.Windowing;

namespace Attractor.Core.Rotation;

public enum RotationState { Stopped, Running, Faulted, Empty }

public enum EngineCommand { Skip, Previous, ToggleHold, Ban, Stop, Reevaluate, Rebag }

public enum GameFaultKind { CrashedAtLaunch, NoWindow, Hung }

/// <summary>Why the rotation moved to a new game. <see cref="Started"/> is the first
/// game of a session; <see cref="Auto"/> is the dwell elapsing on its own; <see
/// cref="Manual"/> is a Skip/Previous/Ban; <see cref="Fault"/> is a broken game being
/// skipped past. Hosts use it e.g. to play a coin chime only on an unattended advance.</summary>
public enum GameChangeReason { Started, Auto, Manual, Fault }

public sealed record RotationOptions
{
    public int DwellSeconds { get; init; } = 300;
    public int WindowTimeoutSeconds { get; init; } = 30;
    public int SettleMs { get; init; } = 500;
    /// <summary>Exits faster than this with a nonzero code = crash, not a finished chunk.</summary>
    public int CrashWindowSeconds { get; init; } = 10;
    /// <summary>Wall-clock watchdog = chunk * factor + base; guards SMB stalls and slow-clock drivers.</summary>
    public int WatchdogFactor { get; init; } = 2;
    public int WatchdogBaseSeconds { get; init; } = 60;
}

/// <summary>The rotation switched to <paramref name="Game"/>; <paramref name="Reason"/> says why.</summary>
public sealed record GameChange(string Game, GameChangeReason Reason);

/// <summary>A fresh MAME window for the current chunk, ready to be embedded by the host UI.</summary>
public sealed record MameWindowReady(string Game, int Pid, IntPtr Hwnd, PixelSize NativeClientSize, int ChunkSeconds);

public sealed record GameFault(string Game, GameFaultKind Kind, FaultVerdict Verdict, int? ExitCode);

/// <summary>
/// Window lookup seam so engine tests can run with no real windows.
/// The default implementation delegates to <see cref="MameWindowLocator"/>.
/// </summary>
public interface IGameWindowFinder
{
    Task<IntPtr> FindAsync(int pid, TimeSpan timeout, Func<bool> processHasExited, CancellationToken ct);
    PixelSize GetClientSize(IntPtr hwnd);
}

public sealed class GameWindowFinder : IGameWindowFinder
{
    public Task<IntPtr> FindAsync(int pid, TimeSpan timeout, Func<bool> processHasExited, CancellationToken ct) =>
        MameWindowLocator.FindGameWindowAsync(pid, timeout, processHasExited, ct);

    public PixelSize GetClientSize(IntPtr hwnd) => MameWindowLocator.GetClientSize(hwnd);
}
