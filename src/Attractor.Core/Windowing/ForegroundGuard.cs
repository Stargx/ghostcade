using System.Text;

namespace Attractor.Core.Windowing;

/// <summary>
/// Belt-and-suspenders focus guard for the attract rotation. The primary defence against
/// focus theft is launch-hidden (a hidden MAME window cannot be foregrounded — see
/// <see cref="MameLauncher"/>). This catches the residual case where a MAME process
/// foregrounds one of its windows AFTER it's been revealed — e.g. re-asserting focus once a
/// slow network ROM finally finishes loading, which WS_EX_NOACTIVATE does not stop. It
/// installs a global EVENT_SYSTEM_FOREGROUND WinEvent hook: whenever a MAME window grabs the
/// foreground while armed, it shoves focus straight back to the last real external window the
/// user was in (their editor, a browser, …) — never to Ghostcade itself, never to MAME.
///
/// Threading: <see cref="Install"/>/<see cref="Dispose"/> and every callback run on the
/// thread that installed the hook, which MUST be a message-pumped thread (the WPF UI thread —
/// WINEVENT_OUTOFCONTEXT delivers the callback via that thread's message queue). All state is
/// single-threaded on it; <see cref="Arm"/>/<see cref="Disarm"/> are called only from there.
/// </summary>
public sealed class ForegroundGuard : IDisposable
{
    // Per-arm bounce cap: MAME's own restore fires a non-MAME foreground event so there is no
    // self-perpetuating loop, but a MAME that re-grabs repeatedly during a slow init shouldn't
    // let us thrash the desktop. Reset every chunk (each Arm).
    private const int MaxBouncesPerArm = 8;

    private readonly uint _ownPid = NativeMethods.GetCurrentProcessId();
    private NativeMethods.WinEventProc? _proc; // rooted so user32 never calls a collected delegate
    private IntPtr _hook;
    private IntPtr _prev;   // the external window to restore focus to (never MAME, never us)
    private bool _armed;
    private int _mamePid;   // current attract MAME pid (0 until known); its windows count as theft
    private int _bounces;

    /// <summary>Install the global hook and seed the restore target. Call once, on the
    /// message-pumped UI thread, passing the app's own main window handle.</summary>
    public void Install(IntPtr ownerHwnd)
    {
        if (_hook != IntPtr.Zero)
            return;
        _proc = OnForeground;
        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _proc, idProcess: 0, idThread: 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        // Seed from whatever is foreground now, but never our own window — at install time
        // that is usually Ghostcade itself, and seeding it would make the first residual bounce
        // pull focus TO us, the exact anti-goal. If it's us (or MAME), leave the target empty
        // and wait for the first real external foreground event.
        var fg = NativeMethods.GetForegroundWindow();
        if (IsRestorable(fg))
            _prev = fg;
    }

    /// <summary>Arm the guard for an attract chunk. Pass the chunk's MAME pid once known so any
    /// window of that process (not just class "MAME") is treated as theft. Resets the bounce
    /// cap for the new chunk.</summary>
    public void Arm(int mamePid = 0)
    {
        _armed = true;
        _mamePid = mamePid;
        _bounces = 0;
    }

    /// <summary>Disarm around a play session — the user asked for the controls, so MAME's
    /// activation must be left alone.</summary>
    public void Disarm() => _armed = false;

    private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (ev != NativeMethods.EVENT_SYSTEM_FOREGROUND || idObject != NativeMethods.OBJID_WINDOW || hwnd == IntPtr.Zero)
            return;

        if (IsMame(hwnd))
        {
            // MAME grabbed the foreground. If armed and we know where the user was, shove it back.
            if (_armed && _bounces < MaxBouncesPerArm && _prev != IntPtr.Zero
                && NativeMethods.IsWindow(_prev) && NativeMethods.IsWindowVisible(_prev))
            {
                _bounces++;
                NativeMethods.SetForegroundWindow(_prev);
            }
            return;
        }

        // A real external window took the foreground (the user's editor, a browser, …). That's
        // where focus should return next time MAME grabs. Never record ourselves or MAME.
        if (IsRestorable(hwnd))
            _prev = hwnd;
    }

    // A window belongs to MAME if it's the class "MAME" game window OR any window owned by the
    // current attract MAME process — so a MAME helper window of another class is still treated
    // as theft to bounce, never recorded as the restore target.
    private bool IsMame(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (_mamePid != 0 && pid == (uint)_mamePid)
            return true;
        var sb = new StringBuilder(16);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString() == "MAME";
    }

    private bool IsRestorable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || IsMame(hwnd))
            return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid != _ownPid; // never restore focus to ourselves
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
            NativeMethods.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _proc = null;
    }
}
