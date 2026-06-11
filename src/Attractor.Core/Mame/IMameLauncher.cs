namespace Attractor.Core.Mame;

public interface IMameLauncher
{
    IMameProcess Launch(MameLaunchSpec spec);
}

/// <summary>A running (or exited) MAME process.</summary>
public interface IMameProcess : IDisposable
{
    int Pid { get; }
    bool HasExited { get; }
    int? ExitCode { get; }
    Task WaitForExitAsync(CancellationToken ct = default);
    void Kill();
}
