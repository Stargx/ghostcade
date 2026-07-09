using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Attractor.Core.Windowing;

namespace Attractor.Core.Mame;

/// <summary>
/// Launches MAME via raw CreateProcessW. Two things ProcessStartInfo cannot do
/// are required here:
///  1. SW_HIDE as the startup show command, so an attract chunk's game window is
///     born HIDDEN. SW_SHOWNOACTIVATE only governs MAME's first ShowWindow — it
///     does NOT stop MAME's OSD from later calling SetForegroundWindow on its own
///     window during video init, which steals focus on rigs where the foreground
///     lock is off. A hidden window cannot be foregrounded, so that grab no-ops.
///     The embedder styles it non-activating + owned while still hidden and then
///     reveals it with SW_SHOWNA (see GlueEmbedder). MAME honors this field —
///     SW_HIDE is proven to suppress its window entirely. A play session asked for
///     the controls, so it keeps SW_SHOWNORMAL and is shown activated as usual.
///  2. CREATE_SUSPENDED, so the process joins the kill-on-close job object
///     before it executes a single instruction — no orphan window of escape.
/// CREATE_NO_WINDOW additionally stops console-subsystem MAME builds (0.147)
/// from popping a console when launched from a GUI app.
/// </summary>
public sealed class MameLauncher : IMameLauncher, IDisposable
{
    private readonly JobObject _job = new();

    public IMameProcess Launch(MameLaunchSpec spec)
    {
        var commandLine = new StringBuilder();
        commandLine.Append('"').Append(spec.MameExePath).Append('"');
        foreach (var arg in spec.BuildArgumentList())
            commandLine.Append(' ').Append(QuoteArgument(arg));

        var si = new NativeMethods.STARTUPINFOW
        {
            cb = Marshal.SizeOf<NativeMethods.STARTUPINFOW>(),
            dwFlags = NativeMethods.STARTF_USESHOWWINDOW,
            // A play session is the one launch that SHOULD take focus — the user
            // asked to grab the controls. Attract chunks are born hidden and revealed
            // (non-activating) only after the embedder styles them, so MAME can never
            // steal focus mid-rotation — a hidden window cannot be foregrounded.
            wShowWindow = spec.PlayMode ? NativeMethods.SW_SHOWNORMAL : NativeMethods.SW_HIDE,
        };

        if (!NativeMethods.CreateProcessW(
                spec.MameExePath, commandLine,
                IntPtr.Zero, IntPtr.Zero, inheritHandles: false,
                NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_NO_WINDOW,
                IntPtr.Zero, spec.WorkingDirectory,
                ref si, out var pi))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for {spec.MameExePath}");

        try
        {
            _job.Assign(pi.hProcess);
        }
        catch
        {
            // Never resume a child that couldn't join the kill-on-close job: it is
            // still suspended (has executed nothing), so terminate it right here
            // rather than release the one MAME that could outlive the app.
            NativeMethods.TerminateProcess(pi.hProcess, 1);
            NativeMethods.CloseHandle(pi.hThread);
            NativeMethods.CloseHandle(pi.hProcess);
            throw;
        }
        NativeMethods.ResumeThread(pi.hThread);
        NativeMethods.CloseHandle(pi.hThread);
        NativeMethods.CloseHandle(pi.hProcess);

        return new MameProcess((int)pi.dwProcessId);
    }

    public void Dispose() => _job.Dispose();

    /// <summary>
    /// Quote one argument per CommandLineToArgvW rules — lpCommandLine is a single
    /// string, and an unquoted <c>-rompath D:\Arcade Roms</c> would reach MAME as
    /// two tokens (a fatal "unknown system"). Backslashes double only before a
    /// quote; embedded quotes get a backslash.
    /// </summary>
    public static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
            return arg;
        var sb = new StringBuilder(arg.Length + 2).Append('"');
        int backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            else
                sb.Append('\\', backslashes).Append(c);
            backslashes = 0;
        }
        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private sealed class MameProcess : IMameProcess
    {
        private readonly Process? _process;

        public MameProcess(int pid)
        {
            Pid = pid;
            try
            {
                _process = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                _process = null; // died instantly (bad rom, bad args)
            }
        }

        public int Pid { get; }

        public bool HasExited
        {
            get
            {
                try { return _process is null || _process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public int? ExitCode
        {
            get
            {
                try { return _process is { HasExited: true } p ? p.ExitCode : null; }
                catch (InvalidOperationException) { return null; }
            }
        }

        public async Task WaitForExitAsync(CancellationToken ct = default)
        {
            if (_process is null) return;
            try { await _process.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (InvalidOperationException) { /* already gone */ }
        }

        public void Kill()
        {
            try { _process?.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            catch (Win32Exception) { /* exiting right now */ }
        }

        public void Dispose() => _process?.Dispose();
    }
}
