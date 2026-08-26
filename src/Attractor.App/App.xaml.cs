using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Attractor.Core.Configuration;
using Attractor.Core.Diagnostics;

namespace Attractor.App;

public partial class App : Application
{
    /// <summary>Raw CLI args, captured for spike/automation modes.</summary>
    internal static string[] StartupArgs { get; private set; } = [];

    /// <summary>App-wide logger; a real FileLog once startup gets going.</summary>
    internal static ILog Log { get; private set; } = NullLog.Instance;

    private Mutex? _singleInstance;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        base.OnStartup(e);

        // regression harness: Ghostcade.exe --spike <mame.exe> <game|-> <log>
        if (e.Args.Length > 0 && e.Args[0] == "--spike")
        {
            new SpikeWindow().Show();
            return;
        }

        // two instances would fight over hotkeys and the embedded window
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Attractor.SingleInstance", out bool first);
        if (!first)
        {
            // Derived, never hardcoded: this must track <AssemblyName>, and a literal
            // would drift silently on a rename — the app would still exit, but never
            // foreground the instance already running.
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var other = System.Diagnostics.Process.GetProcessesByName(self.ProcessName)
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is not null)
                SetForegroundWindow(other.MainWindowHandle);
            Shutdown(0);
            return;
        }

        var paths = new AppPaths();
        Log = new FileLog(paths.LogsDir);
        InstallGlobalExceptionHandlers();
        Log.Info($"Ghostcade starting (data: {paths.Root})");

        AppConfig config;
        try
        {
            config = ConfigStore.Load(paths.ConfigFile);
        }
        // IOException too (config.json held by a sync/AV tool, share flake): letting it
        // reach DispatcherUnhandledException would mark it handled with no window ever
        // created — an invisible process squatting on the single-instance mutex.
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Log.Error("config load failed", ex);
            MessageBox.Show(ex.Message, "Ghostcade — config problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        bool wantSetup = e.Args.Contains("--setup")
            || string.IsNullOrWhiteSpace(config.Mame.ExePath)
            || !File.Exists(config.Mame.ExePath);
        if (wantSetup)
        {
            Log.Info("opening first-run setup wizard");
            new SetupWindow(paths, config).Show();
            return;
        }

        var vm = new MainViewModel(config, paths, Log);
        new MainWindow(vm, paths, config, forceRescan: e.Args.Contains("--rescan")).Show();
    }

    /// <summary>
    /// Catch what would otherwise be silent crashes: log them, and for UI-thread
    /// faults keep the app alive with a friendly notice rather than vanishing.
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("unhandled UI exception", args.Exception);
            MessageBox.Show(
                $"Something went wrong:\n\n{args.Exception.Message}\n\n" +
                "It's been logged (File → Open config folder → logs). The app will keep running.",
                "Ghostcade", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("unhandled non-UI exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warn("unobserved task exception", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info($"Ghostcade exiting (code {e.ApplicationExitCode})");
        (Log as IDisposable)?.Dispose();
        base.OnExit(e);
    }
}
