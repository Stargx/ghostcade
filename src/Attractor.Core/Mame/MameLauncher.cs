using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Attractor.Core.Windowing;

namespace Attractor.Core.Mame;

/// <summary>
/// Launches MAME via raw CreateProcessW. Two things ProcessStartInfo cannot do
/// are required here:
///  1. SW_SHOWNOACTIVATE as the startup show command, so MAME's game window
///     appears WITHOUT stealing keyboard focus on every rotation. (SW_HIDE is
///     proven to suppress MAME's window entirely, so MAME honors this field.)
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
            commandLine.Append(' ').Append(arg);

        var si = new NativeMethods.STARTUPINFOW
        {
            cb = Marshal.SizeOf<NativeMethods.STARTUPINFOW>(),
            dwFlags = NativeMethods.STARTF_USESHOWWINDOW,
            wShowWindow = NativeMethods.SW_SHOWNOACTIVATE,
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
        finally
        {
            NativeMethods.ResumeThread(pi.hThread);
            NativeMethods.CloseHandle(pi.hThread);
            NativeMethods.CloseHandle(pi.hProcess);
        }

        return new MameProcess((int)pi.dwProcessId);
    }

    public void Dispose() => _job.Dispose();

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
