using Attractor.Core.Mame;
using Attractor.Core.Rotation;
using Attractor.Core.Windowing;

namespace Attractor.Core.Tests;

/// <summary>Scriptable stand-in for a MAME process.</summary>
public sealed class FakeProcess : IMameProcess
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int? _exitCode;

    public FakeProcess(int pid) => Pid = pid;

    public int Pid { get; }
    public bool HasExited => _exit.Task.IsCompleted;
    public int? ExitCode => HasExited ? _exitCode : null;

    public Task WaitForExitAsync(CancellationToken ct = default) => _exit.Task.WaitAsync(ct);

    public void Kill() => Exit(-9);

    public void Exit(int code)
    {
        _exitCode ??= code;
        _exit.TrySetResult();
    }

    public void Dispose() { }
}

/// <summary>
/// Launch behavior: decides what each fake MAME run does. Returning a process
/// that auto-exits 0 after a delay simulates a chunk finishing naturally.
/// </summary>
public sealed class FakeLauncher : IMameLauncher
{
    private int _nextPid = 1000;
    public List<(MameLaunchSpec Spec, FakeProcess Proc)> Launches { get; } = [];

    /// <summary>Called per launch; configure the process (e.g. schedule auto-exit).</summary>
    public Action<MameLaunchSpec, FakeProcess> OnLaunch { get; set; } =
        (spec, proc) => Task.Delay(80).ContinueWith(_ => proc.Exit(0));

    public IMameProcess Launch(MameLaunchSpec spec)
    {
        var proc = new FakeProcess(Interlocked.Increment(ref _nextPid));
        lock (Launches)
            Launches.Add((spec, proc));
        OnLaunch(spec, proc);
        return proc;
    }
}

/// <summary>Returns a fake hwnd instantly unless the process already exited.</summary>
public sealed class FakeWindowFinder : IGameWindowFinder
{
    public Task<IntPtr> FindAsync(int pid, TimeSpan timeout, Func<bool> processHasExited, CancellationToken ct) =>
        Task.FromResult(processHasExited() ? IntPtr.Zero : new IntPtr(0xBEEF));

    public PixelSize GetClientSize(IntPtr hwnd) => new(224, 288);
}

public sealed class FakeTime : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 6, 11, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

public static class Wait
{
    /// <summary>Poll until a condition holds; fail loudly instead of hanging the suite.</summary>
    public static async Task ForAsync(Func<bool> condition, int timeoutMs = 5000, string? what = null)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException($"condition not reached: {what ?? "unnamed"}");
            await Task.Delay(10);
        }
    }
}
