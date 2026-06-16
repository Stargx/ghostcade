using System.Diagnostics;

namespace Attractor.Core.Mame;

/// <summary>
/// Runs MAME in "verb" mode (-listxml, -verifyroms, -help): stdout captured,
/// no windows. Plain Process here, not CreateProcessW — verbs never open a
/// game window, so none of the launcher's show-command machinery applies.
/// </summary>
public static class MameVerbRunner
{
    public static Process Start(string mameExePath, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = mameExePath,
            WorkingDirectory = Path.GetDirectoryName(mameExePath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {mameExePath}");
    }

    /// <summary>Run to completion, streaming stdout lines to <paramref name="onLine"/>.</summary>
    public static async Task<int> RunLinesAsync(
        string mameExePath, string[] args, Action<string> onLine, CancellationToken ct)
    {
        using var proc = Start(mameExePath, args);
        try
        {
            // drain stderr so a chatty MAME can't fill the pipe and deadlock
            var drainErr = proc.StandardError.ReadToEndAsync(ct);
            while (await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                onLine(line);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            _ = await drainErr.ConfigureAwait(false);
            return proc.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // cancellation/timeout: actually terminate the verb process, don't orphan it
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw;
        }
    }
}
