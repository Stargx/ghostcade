using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Attractor.Core.Mame;
using Attractor.Core.Windowing;

namespace Attractor.App;

/// <summary>
/// Embedding spike window (kept for regression). Manual mode: launch any game from any MAME and watch it
/// embed. Automated mode: `Attractor.exe --spike <mame.exe> <game|-> <log>`
/// runs the embed checklist and writes machine-readable results to the log.
/// Replaced by the real layout in M3.
/// </summary>
public partial class SpikeWindow : Window
{
    private readonly MameLauncher _launcher = new();
    private IMameWindowEmbedder _embedder = new GlueEmbedder();
    private IMameProcess? _proc;
    private PixelRect _lastHostRect;
    private bool _glueWired;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    public SpikeWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += (_, _) => { _proc?.Kill(); _launcher.Dispose(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var args = App.StartupArgs;
        if (args.Length >= 4 && args[0] == "--spike")
        {
            try
            {
                await RunSpikeAsync(args[1], args[2] == "-" ? "" : args[2], args[3]);
            }
            catch (Exception ex)
            {
                var fallback = Path.Combine(Path.GetTempPath(), "attractor-spike-fallback.log");
                var target = Path.IsPathRooted(args[3]) ? args[3] : fallback;
                try { File.AppendAllText(target, $"SPIKE EXCEPTION {ex}\r\n"); }
                catch { try { File.AppendAllText(fallback, $"SPIKE EXCEPTION {ex}\r\n"); } catch { /* nothing left to report to */ } }
                Application.Current.Shutdown(2);
            }
        }
    }

    // ---- shared launch + embed ------------------------------------------------

    private async Task<(IntPtr hwnd, PixelSize native)> LaunchAndEmbedAsync(string mamePath, string game)
    {
        _proc?.Kill();
        _embedder.Release();

        _embedder = ModeBox.SelectedIndex == 1 ? new ReparentEmbedder() : new GlueEmbedder();

        var spec = new MameLaunchSpec(mamePath, game, SecondsToRun: 280);
        _proc = _launcher.Launch(spec);
        Status($"launched pid {_proc.Pid}, waiting for window...");

        var hwnd = await MameWindowLocator.FindGameWindowAsync(
            _proc.Pid, TimeSpan.FromSeconds(30), () => _proc.HasExited);
        if (hwnd == IntPtr.Zero)
        {
            Status(_proc.HasExited ? $"MAME exited (code {_proc.ExitCode})" : "no window after 30s");
            return (IntPtr.Zero, default);
        }

        await Task.Delay(500); // let MAME settle at its native 1x size
        var native = MameWindowLocator.GetClientSize(hwnd);

        _lastHostRect = GetHostPhysicalRect();
        var owner = new WindowInteropHelper(this).Handle;
        _embedder.Embed(hwnd, owner, _lastHostRect, native);
        WireGlue();

        Status($"embedded {native.Width}x{native.Height} native, pid {_proc.Pid}");
        return (hwnd, native);
    }

    private void WireGlue()
    {
        if (_glueWired) return;
        _glueWired = true;
        LayoutUpdated += (_, _) => Reglue();
        LocationChanged += (_, _) => Reglue();
    }

    private void Reglue()
    {
        var rect = GetHostPhysicalRect();
        if (rect == _lastHostRect || rect.Width <= 0)
            return;
        _lastHostRect = rect;
        _embedder.UpdateBounds(rect);
    }

    private PixelRect GetHostPhysicalRect()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is not { } target)
            return default;
        var origin = HostRegion.PointToScreen(new Point(0, 0)); // physical px under PMv2
        var m = target.TransformToDevice;
        return new PixelRect(
            (int)Math.Round(origin.X),
            (int)Math.Round(origin.Y),
            (int)Math.Round(HostRegion.ActualWidth * m.M11),
            (int)Math.Round(HostRegion.ActualHeight * m.M22));
    }

    private void Status(string text) => StatusText.Text = text;

    // ---- manual buttons ---------------------------------------------------------

    private async void LaunchBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await LaunchAndEmbedAsync(MamePathBox.Text.Trim(), GameBox.Text.Trim());
        }
        catch (Exception ex)
        {
            Status($"error: {ex.Message}");
        }
    }

    private void KillBtn_Click(object sender, RoutedEventArgs e)
    {
        _proc?.Kill();
        Status("killed");
    }

    // ---- automated spike --------------------------------------------------------

    private async Task RunSpikeAsync(string mamePath, string game, string logPath)
    {
        var log = new List<string>();
        void L(string line) { log.Add(line); }

        // Relinquish foreground BEFORE launching MAME so this is a background launch — the
        // real ambient-wallpaper scenario (the user is typing in another app). A foreground
        // launcher would confer the one-time SetForegroundWindow grant on the MAME child,
        // masking the real behavior (MAME would then legitimately reach the foreground). The
        // shell/desktop is a convenient neutral window to hand foreground to.
        var shell = GetShellWindow();
        if (shell != IntPtr.Zero)
            SetForegroundWindow(shell);
        await Task.Delay(300);
        var foregroundBefore = GetForegroundWindow();
        L($"BG-LAUNCH relinquished-to-shell foreground=0x{foregroundBefore:X} isThisApp={foregroundBefore == new WindowInteropHelper(this).Handle}");
        var t0 = Environment.TickCount64;

        var (hwnd, native) = await LaunchAndEmbedAsync(mamePath, game);
        if (hwnd == IntPtr.Zero)
        {
            L($"FAIL no-window exited={_proc?.HasExited} exit={_proc?.ExitCode}");
            Finish(false);
            return;
        }
        L($"FOUND ms={Environment.TickCount64 - t0} native={native.Width}x{native.Height}");

        await Task.Delay(1000);

        // 1. window sits exactly on the aspect-fitted rect
        var expected = PixelRect.FitAspect(native, _lastHostRect);
        var actual = MameWindowLocator.GetWindowBounds(hwnd);
        bool rectOk = Near(expected.X, actual.X) && Near(expected.Y, actual.Y) &&
                      Near(expected.Width, actual.Width) && Near(expected.Height, actual.Height);
        L($"RECT expected={expected} actual={actual} ok={rectOk}");

        // 2. focus did not move to MAME (a background-launched attract chunk must never steal it)
        var foregroundAfter = GetForegroundWindow();
        bool focusOk = foregroundAfter != hwnd;
        L($"FOCUS after=0x{foregroundAfter:X} mameForeground={foregroundAfter == hwnd} keptPriorForeground={foregroundAfter == foregroundBefore} ok={focusOk}");

        // 3. glue follows an app-window move
        Left += 60;
        await Task.Delay(400);
        var expected2 = PixelRect.FitAspect(native, GetHostPhysicalRect());
        var actual2 = MameWindowLocator.GetWindowBounds(hwnd);
        bool glueOk = Near(expected2.X, actual2.X) && Near(expected2.Width, actual2.Width);
        L($"GLUE expected={expected2} actual={actual2} ok={glueOk}");

        // 4. kill tears the window down
        _proc!.Kill();
        await Task.Delay(1500);
        bool killOk = _proc.HasExited;
        L($"KILL exited={killOk}");

        bool pass = rectOk && focusOk && glueOk && killOk;
        L($"SPIKE RESULT {(pass ? "PASS" : "FAIL")}");
        File.WriteAllLines(logPath, log);
        Finish(pass);

        void Finish(bool ok)
        {
            if (log.Count > 0) File.WriteAllLines(logPath, log);
            Application.Current.Shutdown(ok ? 0 : 1);
        }

        static bool Near(int a, int b) => Math.Abs(a - b) <= 2;
    }
}

