using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Attractor.Core.Configuration;

namespace Attractor.App;

public partial class App : Application
{
    /// <summary>Raw CLI args, captured for spike/automation modes.</summary>
    internal static string[] StartupArgs { get; private set; } = [];

    private Mutex? _singleInstance;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    protected override void OnStartup(StartupEventArgs e)
    {
        StartupArgs = e.Args;
        base.OnStartup(e);

        // regression harness: Attractor.exe --spike <mame.exe> <game|-> <log>
        if (e.Args.Length > 0 && e.Args[0] == "--spike")
        {
            new SpikeWindow().Show();
            return;
        }

        // two instances would fight over hotkeys and the embedded window
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Attractor.SingleInstance", out bool first);
        if (!first)
        {
            var other = System.Diagnostics.Process.GetProcessesByName("Attractor")
                .FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other is not null)
                SetForegroundWindow(other.MainWindowHandle);
            Shutdown(0);
            return;
        }

        var paths = new AppPaths();

        AppConfig config;
        try
        {
            config = ConfigStore.Load(paths.ConfigFile);
        }
        catch (InvalidDataException ex)
        {
            MessageBox.Show(ex.Message, "Attractor — config problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        bool wantSetup = e.Args.Contains("--setup")
            || string.IsNullOrWhiteSpace(config.Mame.ExePath)
            || !File.Exists(config.Mame.ExePath);
        if (wantSetup)
        {
            new SetupWindow(paths, config).Show();
            return;
        }

        var vm = new MainViewModel(config, paths);
        new MainWindow(vm, paths, config, forceRescan: e.Args.Contains("--rescan")).Show();
    }
}
